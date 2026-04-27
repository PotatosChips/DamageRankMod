using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using System;

namespace FH_DamageRankMod
{
    [ModInitializer(nameof(Initialize))]
    public static class FH_DamageRankMod
    {
        // 防止重复注册退出事件，避免在退出时重复保存。
        private static bool _exitHandlerRegistered;

        public static void Initialize()
        {
            Log.Info("FH_DamageRankMod - 加载成功!");

            // 应用所有 Harmony Patch（伤害统计、战斗生命周期等）。
            var harmony = new Harmony("FH_DamageRankMod");
            harmony.PatchAll();

            // 启动时先读取历史累计数据，保证 UI 展示连续。
            DamageTracker.LoadPersistentData();

            // 注册进程退出保存逻辑，降低异常退出导致的数据丢失。
            RegisterExitHandler();

            var tree = Engine.GetMainLoop() as SceneTree;
            if (tree == null)
            {
                Log.Error("SceneTree 获取失败");
                return;
            }

            var damageDisplay = new DamageDisplay();

            // 使用 Deferred 避免在不安全时机直接改场景树。
            tree.CurrentScene.CallDeferred("add_child", damageDisplay);
        }

        private static void RegisterExitHandler()
        {
            if (_exitHandlerRegistered)
            {
                return;
            }

            _exitHandlerRegistered = true;
            // 两个事件都监听，确保尽可能在退出时落盘。
            AppDomain.CurrentDomain.ProcessExit += (_, _) => DamageTracker.FlushUnfinishedBattleAndSave();
            AppDomain.CurrentDomain.DomainUnload += (_, _) => DamageTracker.FlushUnfinishedBattleAndSave();
        }
    }
}