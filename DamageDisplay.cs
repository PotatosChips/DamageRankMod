using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Platform;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Logging;
using System;

namespace FH_DamageRankMod
{
    public partial class DamageDisplay : CanvasLayer
    {
        // 每个玩家行对应的一组 UI 控件和临时状态（高亮结束时间等）。
        private sealed class PlayerRowUi
        {
            public PanelContainer RowPanel { get; set; }
            public Label RankLabel { get; set; }
            public Label MainLineLabel { get; set; }
            public Label SubLineLabel { get; set; }
        }

        private readonly Dictionary<ulong, string> _nameCache = new();
        private readonly Dictionary<ulong, PlayerRowUi> _rowUiByNetId = new();

        private PanelContainer _rootPanel;
        private VBoxContainer _rowsContainer;
        private Label _titleLabel;
        private Label _summaryLabel;
        private VBoxContainer _contentContainer;
        private HBoxContainer _headerRow;
        private Button _collapseButton;

        private bool _uiBuilt;
        private bool _dirty;
        private bool _showTotalDamage = true;
        private bool _showShare = true;
        private float _refreshIntervalSec = 0.15f;
        private double _nextRenderAtSec;
        private bool _isDragging;
        private float _uiScale = 0.88f;
        private bool _isCollapsed;
        private readonly Vector2 _expandedMinSize = new(430, 220);
        private readonly Vector2 _collapsedMinSize = new(430, 58);

        // 当前战斗上下文，来自 CombatStateNotifier 事件。
        public CombatState combatState { get; set; }

        public override void _Ready()
        {
            // 订阅数据事件：战斗初始化 + 伤害变化。
            CombatStateNotifier.OnCombatStateInitialized += GetCombatState;
            DamageDisplayUpdater.OndamageChanged += UIdisplay;
        }

        public override void _ExitTree()
        {
            // 节点销毁时解绑，避免重复订阅或悬挂引用。
            CombatStateNotifier.OnCombatStateInitialized -= GetCombatState;
            DamageDisplayUpdater.OndamageChanged -= UIdisplay;
        }

        public override void _Process(double delta)
        {
            // 使用 dirty + 刷新间隔节流，避免每帧重建文本造成不必要开销。
            if (!_uiBuilt || !_dirty)
            {
                return;
            }

            var now = Time.GetTicksMsec() / 1000.0;
            if (now < _nextRenderAtSec)
            {
                return;
            }

            RenderSnapshot();
            _dirty = false;
            _nextRenderAtSec = now + _refreshIntervalSec;
        }

        private void BuildPanel()
        {
            if (_uiBuilt)
            {
                return;
            }

            Layer = 100;

            _rootPanel = new PanelContainer
            {
                CustomMinimumSize = _expandedMinSize
            };
            var panelStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.08f, 0.09f, 0.13f, 0.84f),
                BorderColor = new Color(0.82f, 0.74f, 0.42f, 0.88f),
                BorderWidthBottom = 2,
                BorderWidthLeft = 2,
                BorderWidthRight = 2,
                BorderWidthTop = 2,
                CornerRadiusBottomLeft = 8,
                CornerRadiusBottomRight = 8,
                CornerRadiusTopLeft = 8,
                CornerRadiusTopRight = 8
            };
            _rootPanel.AddThemeStyleboxOverride("panel", panelStyle);

            var outerMargin = new MarginContainer();
            outerMargin.AddThemeConstantOverride("margin_left", 12);
            outerMargin.AddThemeConstantOverride("margin_right", 12);
            outerMargin.AddThemeConstantOverride("margin_top", 10);
            outerMargin.AddThemeConstantOverride("margin_bottom", 10);
            _rootPanel.AddChild(outerMargin);

            var rootColumn = new VBoxContainer();
            rootColumn.AddThemeConstantOverride("separation", 6);
            outerMargin.AddChild(rootColumn);

            _headerRow = new HBoxContainer();
            _headerRow.AddThemeConstantOverride("separation", 8);
            rootColumn.AddChild(_headerRow);

            _titleLabel = new Label
            {
                Text = "Damage Board"
            };
            _titleLabel.AddThemeFontSizeOverride("font_size", 17);
            _titleLabel.AddThemeColorOverride("font_color", new Color(0.98f, 0.92f, 0.66f));
            _titleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _headerRow.AddChild(_titleLabel);
            // 事件交给根面板处理，确保标题整块区域都能拖动。
            _titleLabel.MouseFilter = Control.MouseFilterEnum.Ignore;

            _collapseButton = new Button
            {
                Text = "−",
                CustomMinimumSize = new Vector2(34, 24)
            };
            _collapseButton.Pressed += ToggleCollapsed;
            _headerRow.AddChild(_collapseButton);

            _contentContainer = new VBoxContainer();
            _contentContainer.AddThemeConstantOverride("separation", 6);
            rootColumn.AddChild(_contentContainer);

            _summaryLabel = new Label
            {
                Text = "本场总伤害: 0 | 累计总伤害: 0"
            };
            _summaryLabel.AddThemeFontSizeOverride("font_size", 13);
            _summaryLabel.AddThemeColorOverride("font_color", new Color(0.82f, 0.84f, 0.9f));
            _contentContainer.AddChild(_summaryLabel);

            _rowsContainer = new VBoxContainer();
            _rowsContainer.AddThemeConstantOverride("separation", 4);
            _contentContainer.AddChild(_rowsContainer);

            AddChild(_rootPanel);
            // 统一在根面板处理拖拽输入，减少分散事件处理。
            _rootPanel.GuiInput += OnPanelGuiInput;
            PlacePanelAtTopRightGoldenRatio();
            _uiBuilt = true;
        }

        private void OnPanelGuiInput(InputEvent @event)
        {
            // 鼠标左键按下且落在标题区域时开始拖拽，抬起时结束拖拽。
            if (@event is InputEventMouseButton buttonEvent && buttonEvent.ButtonIndex == MouseButton.Left)
            {
                if (buttonEvent.Pressed && IsInTitleBar(buttonEvent.Position))
                {
                    _isDragging = true;
                    GetViewport().SetInputAsHandled();
                }
                else if (!buttonEvent.Pressed)
                {
                    _isDragging = false;
                }
            }

            if (@event is InputEventMouseMotion motionEvent && _isDragging)
            {
                // 拖动时实时限位，防止面板被拖出屏幕可见区域。
                var candidate = _rootPanel.Position + motionEvent.Relative;
                _rootPanel.Position = ClampToViewport(candidate);
                GetViewport().SetInputAsHandled();
            }
        }

        private bool IsInTitleBar(Vector2 localPosition)
        {
            // 标题栏整条可拖动（按钮区域除外，避免点按钮时触发拖动）。
            var panelWidth = Mathf.Max(_rootPanel.Size.X, _rootPanel.CustomMinimumSize.X);
            const float headerHeight = 56f;
            var inHeader = localPosition.X >= 0f
                && localPosition.X <= panelWidth
                && localPosition.Y >= 0f
                && localPosition.Y <= headerHeight;

            if (!inHeader)
            {
                return false;
            }

            if (_collapseButton == null)
            {
                return true;
            }

            var buttonRect = new Rect2(_collapseButton.Position, _collapseButton.Size);
            return !buttonRect.HasPoint(localPosition);
        }

        private Vector2 ClampToViewport(Vector2 position)
        {
            var viewport = GetViewport();
            if (viewport == null)
            {
                return position;
            }

            // 根据视口和面板尺寸计算可移动边界。
            var viewportSize = viewport.GetVisibleRect().Size;
            var panelSize = _rootPanel.Size;
            var maxX = Mathf.Max(0f, viewportSize.X - panelSize.X);
            var maxY = Mathf.Max(0f, viewportSize.Y - panelSize.Y);
            return new Vector2(
                Mathf.Clamp(position.X, 0f, maxX),
                Mathf.Clamp(position.Y, 0f, maxY)
            );
        }

        private void PlacePanelAtTopRightGoldenRatio()
        {
            var viewport = GetViewport();
            if (viewport == null)
            {
                _rootPanel.Position = new Vector2(12f, 12f);
                return;
            }

            // 初始放在右上偏内侧，避免贴边太紧，也尽量不挡住主战斗区域。
            var viewportSize = viewport.GetVisibleRect().Size;
            var panelWidth = Mathf.Max(_rootPanel.Size.X, _rootPanel.CustomMinimumSize.X);
            var panelHeight = Mathf.Max(_rootPanel.Size.Y, _rootPanel.CustomMinimumSize.Y);

            const float phi = 0.618f;
            var rightPadding = Mathf.Max(18f, viewportSize.X * (1f - phi) * 0.12f);
            var topPadding = Mathf.Max(16f, viewportSize.Y * 0.04f);

            var targetPosition = new Vector2(
                viewportSize.X - panelWidth - rightPadding,
                topPadding
            );

            _rootPanel.Position = ClampToViewport(targetPosition);
        }

        public void UIdisplay(int damage)
        {
            // 只标记脏，不立即渲染；真正渲染由 _Process 的节流逻辑触发。
            _dirty = true;
        }

        private void RenderSnapshot()
        {
            if (combatState == null || !_uiBuilt)
            {
                return;
            }

            var snapshot = DamageUiModel.BuildSnapshot(combatState, GetOnlineNameCached);
            _summaryLabel.Text = $"本场总伤害: {snapshot.BattleTotal} | 累计总伤害: {snapshot.CampaignTotal}";

            // 先删除已离开快照的行，再按快照更新/创建行，保证 UI 与数据一致。
            var presentIds = new HashSet<ulong>(snapshot.Rows.Select(x => x.NetId));
            RemoveRowsNotInSnapshot(presentIds);

            for (var i = 0; i < snapshot.Rows.Count; i++)
            {
                var row = snapshot.Rows[i];
                // 尝试通过玩家NetId查找UI → 找不到就进入if
                if (!_rowUiByNetId.TryGetValue(row.NetId, out var ui))
                {
                    // 找不到 → 创建新的玩家行UI
                    ui = CreatePlayerRowUi();

                    // 把新创建的UI存入字典，下次直接用
                    _rowUiByNetId[row.NetId] = ui;
                }

                // 关键：按快照顺序重排容器中的子节点，保证列表视觉顺序和伤害排序一致。
                if (ui.RowPanel.GetParent() == _rowsContainer && ui.RowPanel.GetIndex() != i)
                {
                    _rowsContainer.MoveChild(ui.RowPanel, i);
                }

                ApplyRow(ui, row);
            }
        }

        private PlayerRowUi CreatePlayerRowUi()
        {
            // 每个玩家一行：上方主信息（排名/本场），下方次信息（累计）。
            var rowPanel = new PanelContainer();
            var rowStyle = new StyleBoxFlat
            {
                BgColor = new Color(0.14f, 0.16f, 0.22f, 0.75f),
                CornerRadiusBottomLeft = 6,
                CornerRadiusBottomRight = 6,
                CornerRadiusTopLeft = 6,
                CornerRadiusTopRight = 6
            };
            rowPanel.AddThemeStyleboxOverride("panel", rowStyle);
            //内边距（Margin）
            var rowMargin = new MarginContainer();
            rowMargin.AddThemeConstantOverride("margin_left", 8);
            rowMargin.AddThemeConstantOverride("margin_right", 8);
            rowMargin.AddThemeConstantOverride("margin_top", 6);
            rowMargin.AddThemeConstantOverride("margin_bottom", 6);
            rowPanel.AddChild(rowMargin);
            //垂直布局（上下两行)
            var column = new VBoxContainer();
            column.AddThemeConstantOverride("separation", 1);
            rowMargin.AddChild(column);
            // 上行：排名 + 主信息
            var topLine = new HBoxContainer();
            topLine.AddThemeConstantOverride("separation", 8);
            column.AddChild(topLine);
            // 排名标签，初始文本占位，后续根据数据更新。
            var rankLabel = new Label { Text = "#-" };
            rankLabel.AddThemeFontSizeOverride("font_size", 14);
            topLine.AddChild(rankLabel);
            // 主信息标签
            var mainLineLabel = new Label { Text = "玩家 | 本场: 0 | 占比: 0%" };
            mainLineLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            mainLineLabel.AddThemeFontSizeOverride("font_size", 14);
            topLine.AddChild(mainLineLabel);
            // 下行：次要信息
            var subLineLabel = new Label { Text = "总伤害: 0" };
            subLineLabel.AddThemeFontSizeOverride("font_size", 12);
            subLineLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.73f, 0.79f));
            column.AddChild(subLineLabel);

            _rowsContainer.AddChild(rowPanel);

            return new PlayerRowUi
            {
                RowPanel = rowPanel,
                RankLabel = rankLabel,
                MainLineLabel = mainLineLabel,
                SubLineLabel = subLineLabel
            };
        }

        private void ApplyRow(PlayerRowUi ui, DamageUiRow row)
        {
            var rankColor = GetRankColor(row.Rank);
            ui.RankLabel.Text = $"#{row.Rank}";
            ui.MainLineLabel.AddThemeColorOverride("font_color", rankColor);
            ui.RankLabel.AddThemeColorOverride("font_color", rankColor);
            var mainText = $"{row.Name} | 本场: {row.BattleDamage}";
            if (_showShare)
            {
                mainText += $" | 占比: {(row.BattleShare * 100f):0.#}%";
            }

            ui.MainLineLabel.Text = mainText;
            ui.SubLineLabel.Text = _showTotalDamage ? $"总伤害: {row.TotalDamage}" : string.Empty;
            ui.SubLineLabel.Visible = _showTotalDamage;
        }

        private void RemoveRowsNotInSnapshot(HashSet<ulong> presentIds)
        {
            // 快照中不存在的玩家行需要释放，避免 UI 残留和缓存膨胀。
            var staleIds = _rowUiByNetId.Keys.Where(id => !presentIds.Contains(id)).ToList();
            foreach (var staleId in staleIds)
            {
                var ui = _rowUiByNetId[staleId];
                ui.RowPanel.QueueFree();
                _rowUiByNetId.Remove(staleId);
            }
        }

        private Color GetRankColor(int rank)
        {
            if (rank == 1) return new Color(1, 0.25f, 0.25f);
            if (rank == 2) return new Color(0.25f, 0.5f, 1);
            if (rank == 3) return new Color(1, 0.9f, 0.2f);
            return new Color(0.74f, 0.78f, 0.85f);
        }

        private void ToggleCollapsed()
        {
            _isCollapsed = !_isCollapsed;
            _contentContainer.Visible = !_isCollapsed;
            _collapseButton.Text = _isCollapsed ? "+" : "−";
            _rootPanel.CustomMinimumSize = _isCollapsed ? _collapsedMinSize : _expandedMinSize;

            // 真正折叠：不仅隐藏内容，也同步收缩面板高度。
            _rootPanel.ResetSize();
            var targetSize = _isCollapsed ? _collapsedMinSize : _expandedMinSize;
            _rootPanel.Size = targetSize;
            _rootPanel.Position = ClampToViewport(_rootPanel.Position);
        }

        private string GetOnlineNameCached(ulong playerId)
        {
            // 玩家名优先走缓存，避免频繁访问平台接口。
            if (_nameCache.TryGetValue(playerId, out var cached))
            {
                return cached;
            }

            var platform = PlatformUtil.PrimaryPlatform;
            var name = PlatformUtil.GetPlayerName(platform, playerId);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Player-{playerId}";
            }

            Log.Info($"获取玩家名字: playerId={playerId}, name={name}");
            _nameCache[playerId] = name;
            return name;
        }

        private void GetCombatState(CombatState combatstate)
        {
            combatState = combatstate;
            Log.Info($"CombatState 已通过事件通知成功传递: {combatState}");

            // 首次拿到战斗上下文时构建面板，并强制下一帧立即渲染一次。
            BuildPanel();
            _dirty = true;
            _nextRenderAtSec = 0;
        }
    }
}
