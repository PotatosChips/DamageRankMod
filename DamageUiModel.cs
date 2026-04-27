using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FH_DamageRankMod
{
    // 一次 UI 渲染所需的完整快照：玩家行数据 + 汇总数据。
    internal sealed class DamageUiSnapshot
    {
        public IReadOnlyList<DamageUiRow> Rows { get; }
        public int BattleTotal { get; }
        public int CampaignTotal { get; }

        public DamageUiSnapshot(IReadOnlyList<DamageUiRow> rows, int battleTotal, int campaignTotal)
        {
            Rows = rows;
            BattleTotal = battleTotal;
            CampaignTotal = campaignTotal;
        }
    }

    internal sealed class DamageUiRow
    {
        public int Rank { get; }
        public ulong NetId { get; }
        public string Name { get; }
        public int BattleDamage { get; }
        public int TotalDamage { get; }
        public float BattleShare { get; }

        public DamageUiRow(int rank, ulong netId, string name, int battleDamage, int totalDamage, float battleShare)
        {
            Rank = rank;
            NetId = netId;
            Name = name;
            BattleDamage = battleDamage;
            TotalDamage = totalDamage;
            BattleShare = battleShare;
        }
    }

    internal static class DamageUiModel
    {
        // 将 CombatState + DamageTracker 里的数据整理为可直接渲染的 UI 快照。
        public static DamageUiSnapshot BuildSnapshot(CombatState combatState, Func<ulong, string> resolveName)
        {
            var players = combatState?.Players;
            if (players == null)
            {
                return new DamageUiSnapshot(new List<DamageUiRow>(), 0, 0);
            }
            var battleTotal = 0;
            var campaignTotal = 0;

            var ordered = new List<(ulong netId, string name, int battleDamage, int totalDamage)>();

            // 先收集每个玩家的本场/累计伤害，并同步计算总和。
            foreach (var player in players)
            {
                var netId = player.NetId;
                var battleDamage = DamageTracker.GetFinalDamage(netId);
                var totalDamage = DamageTracker.GetTotalFinalDamage(netId);
                ordered.Add((netId, resolveName(netId), battleDamage, totalDamage));

                battleTotal += battleDamage;
                campaignTotal += totalDamage;
            }

            // 排序规则：
            // 1) 本场伤害高者优先
            // 2) 本场相同则累计高者优先
            // 3) 再按名字排序，保证展示稳定
            ordered = ordered
                .OrderByDescending(x => x.battleDamage)
                .ThenByDescending(x => x.totalDamage)
                .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = new List<DamageUiRow>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                // 避免 battleTotal=0 时除零。
                var share = battleTotal <= 0 ? 0f : (float)entry.battleDamage / battleTotal;
                rows.Add(new DamageUiRow(i + 1, entry.netId, entry.name, entry.battleDamage, entry.totalDamage, share));
            }

            return new DamageUiSnapshot(rows, battleTotal, campaignTotal);
        }
    }
}
