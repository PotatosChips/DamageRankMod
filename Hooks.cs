using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FH_DamageRankMod
{
    internal static class Hooks
    {
        // ==============================================
        // 伤害统计入口：拦截 CreatureCmd.Damage，按玩家 NetId 归集最终伤害
        // ==============================================
        [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage),
            new[] {
                typeof(PlayerChoiceContext),
                typeof(IEnumerable<Creature>),
                typeof(decimal),
                typeof(ValueProp),
                typeof(Creature),
                typeof(CardModel)
            })]
        public static class CreatureCmd_Damage_Patch
        {
            // 将伤害来源统一映射为玩家 NetId：
            // 1) 玩家直接造成伤害 -> 玩家自己的 NetId
            // 2) 宠物造成伤害 -> 归属到宠物主人 NetId
            static bool TryGetSourcePlayerNetId(Creature dealer, out ulong netId)
            {
                netId = 0;
                if (dealer == null) return false;

                if (dealer.IsPlayer && dealer.Player != null)
                {
                    netId = dealer.Player.NetId;
                    return true;
                }

                // 奥斯提等宠物造成的伤害，归属到宠物主人
                if (dealer.IsPet && dealer.PetOwner != null)
                {
                    netId = dealer.PetOwner.NetId;
                    return true;
                }

                return false;
            }

            // Harmony 里值类型参数若想在 Postfix 使用，常见做法是通过 __state 传递。
            static void Prefix(Creature dealer, decimal amount, out decimal __state)
            {
                __state = amount;
            }

            // 这里是异步 Postfix：先等原 Damage 执行完成拿到结果，再累计最终伤害。
            // 注意：最终伤害可能和原始 amount 不同（受易伤/格挡/减伤等影响）。
            static async void Postfix(
                Creature dealer,
                decimal __state,
                Task<IEnumerable<DamageResult>> __result)
            {
                if (!TryGetSourcePlayerNetId(dealer, out ulong netId)) return;
                var results = await __result;

                int finalDmg = 0;

                foreach (var res in results)
                {
                    finalDmg += (int)res.TotalDamage;
                    Log.Info($"伤害结果: 原始伤害 {__state} \n→ 最终伤害 {res.TotalDamage} \nfinalDmg{finalDmg}\n(NetId: {netId})");
                }
                // 将本次命令造成的总最终伤害累计到该玩家，并通知 UI 尝试刷新。
                DamageTracker.AddFinalDamage(netId, finalDmg);
                DamageDisplayUpdater.Notify(finalDmg);
            }
        }
        // ==============================================
        // 战斗开始/结束 → 重置伤害
        // ==============================================
        [HarmonyPatch(typeof(CombatManager), "StartCombatInternal")]
        public static class CombatManager_StartCombat_Patch
        {
            static void Postfix()
            {
                // 战斗开始时只清空“本场统计”，累计统计继续保留。
                DamageTracker.ResetBattle();

                // CombatState 是 UI 构建和玩家列表读取的关键上下文，初始化后广播给 UI。
                var combatState = CombatManager.Instance.DebugOnlyGetState();
                if (combatState == null)
                {
                    Log.Error("CombatState 未初始化，无法通知 DamageDisplay");
                    return;
                }
                else
                { 
                    Log.Info("CombatState 在 StartCombatInternal 后已初始化");
                    CombatStateNotifier.Notify(combatState);
                }
                }
            }
        }
    public static class CombatStateNotifier
    {
        // DamageDisplay 在 _Ready 中订阅这个事件，拿到当前战斗上下文。
        public static event Action<CombatState> OnCombatStateInitialized;

        public static void Notify(CombatState state)
        {
            OnCombatStateInitialized?.Invoke(state);
        }
    }

    [HarmonyPatch(typeof(CombatManager), "EndCombatInternal")]
        public static class CombatManager_EndCombat_Patch
        {
            // 战斗结束时将“本场”合并进“累计”，然后落盘。
            static void Postfix() => DamageTracker.FinishBattle();
        }

    // ==============================================
    // 新游戏开始（非读档）→ 重置累计伤害
    // ==============================================
    [HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpNewSinglePlayer))]
    public static class RunManager_SetUpNewSinglePlayer_Patch
    {
        static void Postfix()
        {
            DamageTracker.ResetCampaign();
            Log.Info("检测到新单人游戏开始，已重置伤害累计");
        }
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpNewMultiPlayer))]
    public static class RunManager_SetUpNewMultiPlayer_Patch
    {
        static void Postfix()
        {
            DamageTracker.ResetCampaign();
            Log.Info("检测到新多人游戏开始，已重置伤害累计");
        }
    }
    // ==============================================
    // 多人伤害统计（按 NetId 区分自己/队友）
    // ==============================================
    public static class DamageTracker
    {
        // Godot user:// 指向游戏用户数据目录，适合保存轻量持久化数据。
        private const string SaveFilePath = "user://fh_damage_rank_totals.json";

        private sealed class DamageSaveEntry
        {
            public ulong NetId { get; set; }
            public int TotalRawDamage { get; set; }
            public int TotalFinalDamage { get; set; }
        }

        private sealed class DamageSaveData
        {
            public List<DamageSaveEntry> Entries { get; set; } = new();
        }

        // 本场战斗数据（会在开战或结算后清空）
        public static Dictionary<ulong, int> RawDamage = new Dictionary<ulong, int>();
        public static Dictionary<ulong, int> FinalDamage = new Dictionary<ulong, int>();
        
        // 跨战斗累计数据（会写入存档文件）
        public static Dictionary<ulong, int> TotalRawDamage = new Dictionary<ulong, int>();
        public static Dictionary<ulong, int> TotalFinalDamage = new Dictionary<ulong, int>();

        private static bool _isLoaded;
        private static bool _isExitFlushed;
        
        // 战斗结算：把本场数据并入累计，并立即保存到磁盘。
        public static void FinishBattle()
        {
            foreach (var kv in RawDamage) TotalRawDamage[kv.Key] = TotalRawDamage.TryGetValue(kv.Key, out int v) ? v + kv.Value : kv.Value;
            foreach (var kv in FinalDamage) TotalFinalDamage[kv.Key] = TotalFinalDamage.TryGetValue(kv.Key, out int v) ? v + kv.Value : kv.Value;
            ResetBattle();
            SavePersistentData();
        }

        // 只清空本场，不影响累计。
        public static void ResetBattle()
        {
            RawDamage.Clear();
            FinalDamage.Clear();
        }

        // 新开一局（非读档）时调用：同时清空本场和累计，并覆盖保存文件。
        public static void ResetCampaign()
        {
            ResetBattle();
            TotalRawDamage.Clear();
            TotalFinalDamage.Clear();
            SavePersistentData();
        }

        // 增量累加工具：不存在 key 时自动从 0 开始。
        public static void AddRawDamage(ulong netId, int val) => RawDamage[netId] = RawDamage.TryGetValue(netId, out int v) ? v + val : val;
        public static void AddFinalDamage(ulong netId, int val) => FinalDamage[netId] = FinalDamage.TryGetValue(netId, out int v) ? v + val : val;
       
        // ==============================================
        // 【你UI直接调用这些】
        // ==============================================
        // 按 NetId 查询当前值，不存在则返回 0，避免调用端做空判断。
        public static int GetRawDamage(ulong netId) => RawDamage.TryGetValue(netId, out int v) ? v : 0;
        public static int GetFinalDamage(ulong netId) => FinalDamage.TryGetValue(netId, out int v) ? v : 0;

        public static int GetTotalRawDamage(ulong netId) => TotalRawDamage.TryGetValue(netId, out int v) ? v : 0;
        public static int GetTotalFinalDamage(ulong netId) => TotalFinalDamage.TryGetValue(netId, out int v) ? v : 0;

        public static void LoadPersistentData()
        {
            // 防止重复读取文件导致累计重复覆盖或额外开销。
            if (_isLoaded)
            {
                return;
            }

            _isLoaded = true;
            try
            {
                if (!FileAccess.FileExists(SaveFilePath))
                {
                    return;
                }

                using var file = FileAccess.Open(SaveFilePath, FileAccess.ModeFlags.Read);
                var json = file.GetAsText();
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                var loaded = JsonSerializer.Deserialize<DamageSaveData>(json);
                if (loaded?.Entries == null)
                {
                    return;
                }

                // 先清空再回填，保证以内存中最新文件内容为准。
                TotalRawDamage.Clear();
                TotalFinalDamage.Clear();

                foreach (var entry in loaded.Entries)
                {
                    TotalRawDamage[entry.NetId] = entry.TotalRawDamage;
                    TotalFinalDamage[entry.NetId] = entry.TotalFinalDamage;
                }

                Log.Info($"DamageTracker 已加载历史累计数据: {loaded.Entries.Count} 条");
            }
            catch (Exception ex)
            {
                Log.Error($"DamageTracker 读取累计数据失败: {ex}");
            }
        }

        public static void FlushUnfinishedBattleAndSave()
        {
            // 退出流程可能触发多次（ProcessExit + DomainUnload），只执行一次。
            if (_isExitFlushed)
            {
                return;
            }

            _isExitFlushed = true;

            var hasUnfinishedBattle = RawDamage.Count > 0 || FinalDamage.Count > 0;
            if (hasUnfinishedBattle)
            {
                // 游戏退出后会重开本场战斗，因此未结束战斗的数据不计入累计。
                Log.Info("检测到未结束战斗，退出前丢弃本场临时伤害统计");
                ResetBattle();
            }

            SavePersistentData();
        }

        public static void SavePersistentData()
        {
            try
            {
                // 两张表的 key 可能不完全一致，先取并集再统一序列化。
                var allNetIds = new HashSet<ulong>(TotalRawDamage.Keys);
                allNetIds.UnionWith(TotalFinalDamage.Keys);

                var saveData = new DamageSaveData
                {
                    Entries = allNetIds
                        .Select(netId => new DamageSaveEntry
                        {
                            NetId = netId,
                            TotalRawDamage = GetTotalRawDamage(netId),
                            TotalFinalDamage = GetTotalFinalDamage(netId)
                        })
                        .ToList()
                };

                var json = JsonSerializer.Serialize(saveData);
                using var file = FileAccess.Open(SaveFilePath, FileAccess.ModeFlags.Write);
                file.StoreString(json);
            }
            catch (Exception ex)
            {
                Log.Error($"DamageTracker 保存累计数据失败: {ex}");
            }
        }
    }
    public static class DamageDisplayUpdater
    {
        // 伤害变化事件：UI 层订阅后把自己标记为 dirty，在下一帧节流刷新。
        public static event Action<int> OndamageChanged;

        public static void Notify(int damagechange)
        {
            OndamageChanged?.Invoke(damagechange);
        }
    }
}