using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;
using EveOPreview.Configuration;
using EveOPreview.Configuration.Implementation;
using EveOPreview.Mediator.Messages;
using EveOPreview.UI.Hotkeys;
using EveOPreview.View;
using Gma.System.MouseKeyHook;
using MediatR;

namespace EveOPreview.Services
{
    sealed class ThumbnailManager : IThumbnailManager
    {
        #region Private constants
        private const int WINDOW_POSITION_THRESHOLD_LOW = -10_000;
        private const int WINDOW_POSITION_THRESHOLD_HIGH = 31_000;
        private const int WINDOW_SIZE_THRESHOLD = 10;
        private const int FORCED_REFRESH_CYCLE_THRESHOLD = 2;
        private const int DEFAULT_LOCATION_CHANGE_NOTIFICATION_DELAY = 2;

        private const string DEFAULT_CLIENT_TITLE = "EVE";
        #endregion

        #region Private fields
        private readonly IMediator _mediator;
        private readonly IProcessMonitor _processMonitor;
        private readonly IWindowManager _windowManager;
        private readonly IThumbnailConfiguration _configuration;
        private readonly DispatcherTimer _thumbnailUpdateTimer;
        private readonly IThumbnailViewFactory _thumbnailViewFactory;
        private readonly Dictionary<IntPtr, IThumbnailView> _thumbnailViews;
        private readonly Dictionary<string, IThumbnailView> _thumbnailViewsByTitle;
        private readonly HashSet<IntPtr> _knownClientHandles;
        private IKeyboardMouseEvents _keyboardMouseEvents;

        private (IntPtr Handle, string Title) _activeClient;
        private IntPtr _externalApplication;

        private readonly object _locationChangeNotificationSyncRoot;
        private LocationChangeNotification _enqueuedLocationChangeNotification;

        private bool _ignoreViewEvents;
        private bool _isHoverEffectActive;
        private bool _wasDragged;

        private int _refreshCycleCount;
        private int _hideThumbnailsDelay;

        private List<HotkeyHandler> _cycleClientHotkeyHandlers = new List<HotkeyHandler>();

        /// <summary>Mutable holder for the deferred location-change notification.</summary>
        private sealed class LocationChangeNotification
        {
            public IntPtr Handle;
            public string Title;
            public string ActiveClient;
            public Point Location;
            public int Delay;
        }
        #endregion

        public ThumbnailManager(IMediator mediator, IThumbnailConfiguration configuration, IProcessMonitor processMonitor, IWindowManager windowManager, IThumbnailViewFactory factory, IKeyboardMouseEvents keyboardMouseEvents)
        {
            this._mediator = mediator;
            this._processMonitor = processMonitor;
            this._windowManager = windowManager;
            this._configuration = configuration;
            this._thumbnailViewFactory = factory;
            this._keyboardMouseEvents = keyboardMouseEvents;


            this._activeClient = (IntPtr.Zero, ThumbnailManager.DEFAULT_CLIENT_TITLE);

            this.EnableViewEvents();
            this._isHoverEffectActive = false;

            this._refreshCycleCount = 0;
            this._locationChangeNotificationSyncRoot = new object();
            this._enqueuedLocationChangeNotification = new LocationChangeNotification { Handle = IntPtr.Zero, Delay = -1 };

            this._thumbnailViews = new Dictionary<IntPtr, IThumbnailView>();
            this._thumbnailViewsByTitle = new Dictionary<string, IThumbnailView>(StringComparer.OrdinalIgnoreCase);
            this._knownClientHandles = new HashSet<IntPtr>();

            //  DispatcherTimer setup
            this._thumbnailUpdateTimer = new DispatcherTimer();
            this._thumbnailUpdateTimer.Tick += ThumbnailUpdateTimerTick;
            this._thumbnailUpdateTimer.Interval = new TimeSpan(0, 0, 0, 0, configuration.ThumbnailRefreshPeriod);

            this._hideThumbnailsDelay = this._configuration.HideThumbnailsDelay;

            RegisterAllHotkeys(this._configuration.CycleGroups);
        }

        public IThumbnailView GetClientByTitle(string title)
        {
            _thumbnailViewsByTitle.TryGetValue(title, out var view);
            return view;
        }

        public IThumbnailView GetClientByPointer(IntPtr ptr)
        {
            _thumbnailViews.TryGetValue(ptr, out var view);
            return view;
        }

        public IThumbnailView GetActiveClient()
        {
            return GetClientByPointer(this._activeClient.Handle);
        }

        public void SetActive(KeyValuePair<IntPtr, IThumbnailView> newClient)
        {
            try
            {
                this._windowManager.IsCurrentlySwitching = true;

                this.GetActiveClient()?.ClearBorder();

                this._windowManager.ActivateWindow(newClient.Key);
                this.SwitchActiveClient(newClient.Key, newClient.Value.Title);

                newClient.Value.SetHighlight();
                newClient.Value.Refresh(true);
            }
            finally
            {
                this._windowManager.IsCurrentlySwitching = false;
            }
        }

        public void CycleNextClient(bool isForwards, SortedDictionary<int, string> cycleOrder)
        {
            IOrderedEnumerable<KeyValuePair<int, string>> clientOrder = isForwards
                ? cycleOrder.OrderBy(x => x.Key)
                : cycleOrder.OrderByDescending(x => x.Key);

            bool setNextClient = false;

            foreach (var t in clientOrder)
            {
                if (t.Value == _activeClient.Title)
                {
                    setNextClient = true;
                    continue;
                }

                if (!setNextClient)
                {
                    continue;
                }

                if (_thumbnailViewsByTitle.TryGetValue(t.Value, out var view))
                {
                    var ptr = new KeyValuePair<IntPtr, IThumbnailView>(view.Id, view);
                    SetActive(ptr);
                    return;
                }
            }

            // we didn't get a next one. just get the first one from the start.
            foreach (var t in clientOrder)
            {
                if (_thumbnailViewsByTitle.TryGetValue(t.Value, out var view))
                {
                    var ptr = new KeyValuePair<IntPtr, IThumbnailView>(view.Id, view);
                    SetActive(ptr);
                    _activeClient = (ptr.Key, t.Value);
                    return;
                }
            }
        }

        public void RegisterAllHotkeys(List<CycleGroup> cycleGroups)
        {
            // 1. 防御性检查：如果配置里还没有循环组数据，直接返回，防止空引用崩溃
            if (cycleGroups == null)
            {
                return;
            }

            foreach (var cycleGroup in cycleGroups)
            {
                // 2. 二次保护：防止列表里面有空元素
                if (cycleGroup != null)
                {
                    RegisterCycleClientHotkey(cycleGroup);
                }
            }
        }

        public void RegisterCycleClientHotkey(CycleGroup cycleGroup)
        {
            RegisterCycleClientHotkey(cycleGroup.ForwardHotkeys?.Select(x => this._configuration.StringToKey(x)), true, cycleGroup.ClientsOrder);
            RegisterCycleClientHotkey(cycleGroup.BackwardHotkeys?.Select(x => this._configuration.StringToKey(x)), false, cycleGroup.ClientsOrder);
        }

        internal void RegisterCycleClientHotkey(IEnumerable<Keys> keys, bool isForwards, SortedDictionary<int, string> cycleOrder)
        {
            _keyboardMouseEvents.KeyDown += (sender, e) =>
            {
                foreach (var hotkey in keys)
                {
                    // 【核心绝杀】：必须使用 e.KeyData！只有 KeyData 才包含了 Ctrl/Alt/Shift 组合键的状态！
                    if (e.KeyData == hotkey)
                    {
                        if (this._windowManager.IsCurrentlySwitching)
                        {
                            return;
                        }

                        this.CycleNextClient(isForwards, cycleOrder);
                        e.Handled = true;
                        return;
                    }
                }
            };

            _keyboardMouseEvents.KeyUp += (sender, e) =>
            {
                foreach (var hotkey in keys)
                {
                    // 这里也必须同步修改为 KeyData
                    if (e.KeyData == hotkey)
                    {
                        e.Handled = true;
                        return;
                    }
                }
            };
        }

        public void Start()
        {
            this._thumbnailUpdateTimer.Start();

            this.RefreshThumbnails();
        }

        public void Stop()
        {
            this._thumbnailUpdateTimer.Stop();
        }

        private void ThumbnailUpdateTimerTick(object sender, EventArgs e)
        {
            this.UpdateThumbnailsList();
            this.RefreshThumbnails();
        }

        private async void UpdateThumbnailsList()
        {
            this._processMonitor.GetUpdatedProcesses(out ICollection<IProcessInfo> addedProcesses, out ICollection<IProcessInfo> updatedProcesses, out ICollection<IProcessInfo> removedProcesses);

            List<string> viewsAdded = new List<string>();
            List<string> viewsRemoved = new List<string>();

            foreach (IProcessInfo process in addedProcesses)
            {
                IThumbnailView view = this._thumbnailViewFactory.Create(process.Handle, process.Title, this._configuration.ThumbnailSize);
                view.IsOverlayEnabled = this._configuration.ShowThumbnailOverlays;
                view.SetFrames(this._configuration.ShowThumbnailFrames);
                // Max/Min size limitations should be set AFTER the frames are disabled
                // Otherwise thumbnail window will be unnecessary resized
                view.SetSizeLimitations(this._configuration.ThumbnailMinimumSize, this._configuration.ThumbnailMaximumSize);
                view.SetTopMost(this._configuration.ShowThumbnailsAlwaysOnTop);

                view.ThumbnailLocation = this.IsManageableThumbnail(view)
                                            ? this._configuration.GetThumbnailLocation(view.Title, this._activeClient.Title, view.ThumbnailLocation)
                                            : this._configuration.LoginThumbnailLocation;

                this._thumbnailViews.Add(view.Id, view);
                this._thumbnailViewsByTitle[view.Title] = view;
                this._knownClientHandles.Add(view.Id);

                view.ThumbnailResized = this.ThumbnailViewResized;
                view.ThumbnailMoved = this.ThumbnailViewMoved;
                view.ThumbnailFocused = this.ThumbnailViewFocused;
                view.ThumbnailLostFocus = this.ThumbnailViewLostFocus;
                view.ThumbnailActivated = this.ThumbnailActivated;
                view.ThumbnailDeactivated = this.ThumbnailDeactivated;
                view.RegisterHotkey(this._configuration.GetClientHotkey(view.Title));

                this.ApplyClientLayout(view.Id, view.Title);

                // TODO Add extension filter here later
                if (view.Title != ThumbnailManager.DEFAULT_CLIENT_TITLE)
                {
                    viewsAdded.Add(view.Title);
                }
            }

            foreach (IProcessInfo process in updatedProcesses)
            {
                this._thumbnailViews.TryGetValue(process.Handle, out IThumbnailView view);

                if (view == null)
                {
                    // Something went terribly wrong
                    continue;
                }

                if (process.Title != view.Title) // update thumbnail title
                {
                    viewsRemoved.Add(view.Title);
                    _thumbnailViewsByTitle.Remove(view.Title);
                    view.Title = process.Title;
                    _thumbnailViewsByTitle[view.Title] = view;
                    viewsAdded.Add(view.Title);

                    view.RegisterHotkey(this._configuration.GetClientHotkey(process.Title));

                    this.ApplyClientLayout(view.Id, view.Title);
                }
            }

            foreach (IProcessInfo process in removedProcesses)
            {
                IThumbnailView view = this._thumbnailViews[process.Handle];

                this._thumbnailViews.Remove(view.Id);
                this._thumbnailViewsByTitle.Remove(view.Title);
                this._knownClientHandles.Remove(view.Id);
                if (view.Title != ThumbnailManager.DEFAULT_CLIENT_TITLE)
                {
                    viewsRemoved.Add(view.Title);
                }

                view.UnregisterHotkey();

                view.ThumbnailResized = null;
                view.ThumbnailMoved = null;
                view.ThumbnailFocused = null;
                view.ThumbnailLostFocus = null;
                view.ThumbnailActivated = null;

                view.Close();
            }

            if ((viewsAdded.Count > 0) || (viewsRemoved.Count > 0))
            {
                await this._mediator.Publish(new ThumbnailListUpdated(viewsAdded, viewsRemoved));
            }
        }

        private void RefreshThumbnails()
        {
            // 切换客户端过程中跳过刷新，防止底层窗口闪烁
            if (this._windowManager.IsCurrentlySwitching)
            {
                return;
            }

            // TODO Split this method
            IntPtr foregroundWindowHandle = this._windowManager.GetForegroundWindowHandle();

            // The foreground window can be NULL in certain circumstances, such as when a window is losing activation.
            // It is safer to just skip this refresh round than to do something while the system state is undefined
            if (foregroundWindowHandle == IntPtr.Zero)
            {
                return;
            }

            string foregroundWindowTitle = null;

            // Check if the foreground window handle is one of the known handles for client windows or their thumbnails
            bool isClientWindow = this.IsClientWindowActive(foregroundWindowHandle);
            bool isMainWindowActive = this.IsMainWindowActive(foregroundWindowHandle);

            if (foregroundWindowHandle == this._activeClient.Handle)
            {
                foregroundWindowTitle = this._activeClient.Title;
            }
            else if (this._thumbnailViews.TryGetValue(foregroundWindowHandle, out IThumbnailView foregroundView))
            {
                // This code will work only on Alt+Tab switch between clients
                foregroundWindowTitle = foregroundView.Title;
            }
            else if (!isClientWindow)
            {
                this._externalApplication = foregroundWindowHandle;
            }

            // No need to minimize EVE clients when switching out to non-EVE window (like thumbnail)
            if (!string.IsNullOrEmpty(foregroundWindowTitle))
            {
                this.SwitchActiveClient(foregroundWindowHandle, foregroundWindowTitle);
            }

            bool hideAllThumbnails = this._configuration.HideThumbnailsOnLostFocus && !(isClientWindow || isMainWindowActive);

            // Wait for some time before hiding all previews
            if (hideAllThumbnails)
            {
                this._hideThumbnailsDelay--;
                if (this._hideThumbnailsDelay > 0)
                {
                    hideAllThumbnails = false; // Postpone the 'hide all' operation
                }
                else
                {
                    this._hideThumbnailsDelay = 0; // Stop the counter
                }
            }
            else
            {
                this._hideThumbnailsDelay = this._configuration.HideThumbnailsDelay; // Reset the counter
            }

            this._refreshCycleCount++;

            bool forceRefresh;
            if (this._refreshCycleCount >= ThumbnailManager.FORCED_REFRESH_CYCLE_THRESHOLD)
            {
                this._refreshCycleCount = 0;
                forceRefresh = true;
            }
            else
            {
                forceRefresh = false;
            }

            this.DisableViewEvents();

            // Snap thumbnail
            // No need to update Thumbnails while one of them is highlighted
            if ((!this._isHoverEffectActive) && this.TryDequeueLocationChange(out var locationChange))
            {
                if ((locationChange.ActiveClient == this._activeClient.Title) && this._thumbnailViews.TryGetValue(locationChange.Handle, out var view))
                {
                    this.SnapThumbnailView(view);

                    this.RaiseThumbnailLocationUpdatedNotification(view.Title);
                }
                else
                {
                    this.RaiseThumbnailLocationUpdatedNotification(locationChange.Title);
                }
            }

            // 【新增逻辑】用于记录当前渲染的是第几个未登录窗口
            int loginWindowIndex = 0;

            // Hide, show, resize and move
            foreach (KeyValuePair<IntPtr, IThumbnailView> entry in this._thumbnailViews)
            {
                IThumbnailView view = entry.Value;

                if (hideAllThumbnails || this._configuration.IsThumbnailDisabled(view.Title))
                {
                    if (view.IsActive)
                    {
                        view.Hide();
                    }
                    continue;
                }

                if (this._configuration.HideActiveClientThumbnail && (view.Id == this._activeClient.Handle))
                {
                    if (view.IsActive)
                    {
                        view.Hide();
                    }
                    continue;
                }

                // No need to update Thumbnails while one of them is highlighted
                if (!this._isHoverEffectActive)
                {
                    // 【修改逻辑】区分已识别角色窗口和未识别(未登录)窗口
                    if (this.IsManageableThumbnail(view))
                    {
                        view.ThumbnailLocation = this._configuration.GetThumbnailLocation(view.Title, this._activeClient.Title, view.ThumbnailLocation);
                    }
                    else
                    {
                        // 获取基准坐标 (即通过拖动保存的 LoginThumbnailLocation 坐标)
                        int startX = this._configuration.LoginThumbnailLocation.X;
                        int startY = this._configuration.LoginThumbnailLocation.Y;

                        // 计算垂直偏移量 (窗口高度 + 5像素的垂直间距)
                        int spacing = 5;
                        int offsetY = loginWindowIndex * (this._configuration.ThumbnailSize.Height + spacing);

                        // 应用计算出的坐标
                        view.ThumbnailLocation = new Point(startX, startY + offsetY);

                        // 序号+1，供下一个未登录窗口使用，确保不重叠
                        loginWindowIndex++;
                    }

                    view.SetOpacity(this._configuration.ThumbnailOpacity);
                    view.SetTopMost(this._configuration.ShowThumbnailsAlwaysOnTop);
                }

                view.IsOverlayEnabled = this._configuration.ShowThumbnailOverlays;

                view.SetHighlight(
                    this._configuration.EnableActiveClientHighlight && (view.Id == this._activeClient.Handle),
                    this._configuration.ActiveClientHighlightThickness);

                if (!view.IsActive)
                {
                    view.Show();
                }
                else
                {
                    view.Refresh(forceRefresh);
                }
            }

            this.EnableViewEvents();
        }

        public void UpdateThumbnailsSize()
        {
            this.SetThumbnailsSize(this._configuration.ThumbnailSize);
        }

        private void SetThumbnailsSize(Size size)
        {
            this.DisableViewEvents();

            foreach (KeyValuePair<IntPtr, IThumbnailView> entry in this._thumbnailViews)
            {
                entry.Value.ThumbnailSize = size;
                entry.Value.Refresh(false);
            }

            this.EnableViewEvents();
        }
        
        public void UpdateThumbnailFrames()
        {
            this.DisableViewEvents();

            foreach (KeyValuePair<IntPtr, IThumbnailView> entry in this._thumbnailViews)
            {
                entry.Value.SetFrames(this._configuration.ShowThumbnailFrames);
            }

            this.EnableViewEvents();
        }

        public void UpdateThumbnailTitleFont()
        {
            this.DisableViewEvents();

            foreach (KeyValuePair<IntPtr, IThumbnailView> entry in this._thumbnailViews)
            {
                entry.Value.TitleFontSettings = this._configuration.TitleFontSettings;
                // 让悬浮窗把自己的标题重新赋给自己一次，触发重新读取备注的逻辑
                entry.Value.Title = entry.Value.Title;
            }

            this.EnableViewEvents();
        }

        private void EnableViewEvents()
        {
            this._ignoreViewEvents = false;
        }

        private void DisableViewEvents()
        {
            this._ignoreViewEvents = true;
        }

        private void SwitchActiveClient(IntPtr foregroundClientHandle, string foregroundClientTitle)
        {
            // Check if any actions are needed
            if (this._activeClient.Handle == foregroundClientHandle)
            {
                return;
            }

            // Minimize the currently active client if needed
            if (this._configuration.MinimizeInactiveClients && !this._configuration.IsPriorityClient(this._activeClient.Title))
            {
                this._windowManager.MinimizeWindow(this._activeClient.Handle, false);
            }

            this._activeClient = (foregroundClientHandle, foregroundClientTitle);
        }

        private void ThumbnailViewFocused(IntPtr id)
        {
            if (this._isHoverEffectActive)
            {
                return;
            }

            this._isHoverEffectActive = true;
            this._wasDragged = false;

            IThumbnailView view = this._thumbnailViews[id];

            view.SetTopMost(true);
            view.SetOpacity(1.0);

            if (this._configuration.ThumbnailZoomEnabled)
            {
                this.ThumbnailZoomIn(view);
            }
        }

        private void ThumbnailViewLostFocus(IntPtr id)
        {
            if (!this._isHoverEffectActive)
            {
                return;
            }

            IThumbnailView view = this._thumbnailViews[id];

            if (this._configuration.ThumbnailZoomEnabled)
            {
                this.ThumbnailZoomOut(view);
            }

            view.SetOpacity(this._configuration.ThumbnailOpacity);

            this._isHoverEffectActive = false;

            // 只有发生过拖拽，鼠标离开时才触发吸附；划过不管
            if (this._wasDragged)
            {
                this._wasDragged = false;
                this.SnapThumbnailView(view);

                // 清除排队的通知，防止后续定时器干扰
                lock (this._locationChangeNotificationSyncRoot)
                {
                    var notification = this._enqueuedLocationChangeNotification;
                    notification.Handle = IntPtr.Zero;
                    notification.Delay = -1;
                }
            }
        }

        private void ThumbnailActivated(IntPtr id)
        {
            IThumbnailView view = this._thumbnailViews[id];

            this._windowManager.IsCurrentlySwitching = true;

            Task.Run(() =>
                {
                    this._windowManager.ActivateWindow(view.Id);
                })
                .ContinueWith((task) =>
                {
                    try
                    {
                        // This code should be executed on UI thread
                        this.SwitchActiveClient(view.Id, view.Title);
                        this.UpdateClientLayouts();
                        this.RefreshThumbnails();
                    }
                    finally
                    {
                        this._windowManager.IsCurrentlySwitching = false;
                    }
                }, CancellationToken.None, TaskContinuationOptions.NotOnFaulted, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ThumbnailDeactivated(IntPtr id, bool switchOut)
        {
            if (switchOut)
            {
                this._windowManager.ActivateWindow(this._externalApplication);
            }
            else
            {
                if (!this._thumbnailViews.TryGetValue(id, out IThumbnailView view))
                {
                    return;
                }

                this._windowManager.MinimizeWindow(view.Id, true);
                this.RefreshThumbnails();
            }
        }

        private async void ThumbnailViewResized(IntPtr id)
        {
            if (this._ignoreViewEvents)
            {
                return;
            }

            IThumbnailView view = this._thumbnailViews[id];

            this.SetThumbnailsSize(view.ThumbnailSize);

            view.Refresh(false);

            await this._mediator.Publish(new ThumbnailActiveSizeUpdated(view.ThumbnailSize));
        }

        private void ThumbnailViewMoved(IntPtr id)
        {
            if (this._ignoreViewEvents)
            {
                return;
            }

            this._wasDragged = true;

            IThumbnailView view = this._thumbnailViews[id];
            view.Refresh(false);
            this.EnqueueLocationChange(view);
        }

        // Checks whether currently active window belongs to an EVE client or its thumbnail
        private bool IsClientWindowActive(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }

            // Fast path: check if the handle is directly tracked
            if (_knownClientHandles.Contains(windowHandle))
            {
                return true;
            }

            // Fallback: check each view's IsKnownHandle for overlay/handle matches
            foreach (KeyValuePair<IntPtr, IThumbnailView> entry in this._thumbnailViews)
            {
                if (entry.Value.IsKnownHandle(windowHandle))
                {
                    _knownClientHandles.Add(windowHandle);
                    return true;
                }
            }

            return false;
        }

        // Check whether the currently active window belongs to EVE-O Preview itself
        private bool IsMainWindowActive(IntPtr windowHandle)
        {
            return (this._processMonitor.GetMainProcess().Handle == windowHandle);
        }

        private void ThumbnailZoomIn(IThumbnailView view)
        {
            this.DisableViewEvents();

            view.ZoomIn(ViewZoomAnchorConverter.Convert(this._configuration.ThumbnailZoomAnchor), this._configuration.ThumbnailZoomFactor);
            view.Refresh(false);

            this.EnableViewEvents();
        }

        private void ThumbnailZoomOut(IThumbnailView view)
        {
            this.DisableViewEvents();

            view.ZoomOut();
            view.Refresh(false);

            this.EnableViewEvents();
        }

        private void SnapThumbnailView(IThumbnailView view)
        {
            // 检查此功能是否开启
            if (!this._configuration.EnableThumbnailSnap)
            {
                return;
            }

            // 只对无边框缩略图执行吸附
            if (this._configuration.ShowThumbnailFrames)
            {
                return;
            }

            int gap = 1;
            int thresholdX = 150;
            int thresholdY = 150;

            Point bestLocation = view.ThumbnailLocation;
            int bestDistance = int.MaxValue;

            foreach (var entry in this._thumbnailViews)
            {
                IThumbnailView testView = entry.Value;
                if (view.Id == testView.Id) continue;

                int viewX   = view.ThumbnailLocation.X;
                int viewY   = view.ThumbnailLocation.Y;
                int testX   = testView.ThumbnailLocation.X;
                int testY   = testView.ThumbnailLocation.Y;
                int viewW   = view.ThumbnailSize.Width;
                int viewH   = view.ThumbnailSize.Height;
                int testW   = testView.ThumbnailSize.Width;
                int testH   = testView.ThumbnailSize.Height;

                // 1. 水平对齐（左/右）
                if (Math.Abs(viewY - testY) < thresholdY)
                {
                    // 左侧：view.Right = test.Left - gap → viewX = testX - viewW - gap
                    if (Math.Abs(viewX - (testX - viewW - gap)) < thresholdX)
                    {
                        int nx = testX - viewW - gap;
                        int dist = Math.Abs(viewX - nx) + Math.Abs(viewY - testY);
                        if (dist < bestDistance) { bestDistance = dist; bestLocation = new Point(nx, testY); }
                    }
                    // 右侧：view.Left = test.Right + gap → viewX = testX + testW + gap
                    if (Math.Abs(viewX - (testX + testW + gap)) < thresholdX)
                    {
                        int nx = testX + testW + gap;
                        int dist = Math.Abs(viewX - nx) + Math.Abs(viewY - testY);
                        if (dist < bestDistance) { bestDistance = dist; bestLocation = new Point(nx, testY); }
                    }
                }

                // 2. 垂直对齐（上/下）
                if (Math.Abs(viewX - testX) < thresholdX)
                {
                    // 上方：view.Bottom = test.Top - gap → viewY = testY - viewH - gap
                    if (Math.Abs(viewY - (testY - viewH - gap)) < thresholdY)
                    {
                        int ny = testY - viewH - gap;
                        int dist = Math.Abs(viewX - testX) + Math.Abs(viewY - ny);
                        if (dist < bestDistance) { bestDistance = dist; bestLocation = new Point(testX, ny); }
                    }
                    // 下方：view.Top = test.Bottom + gap → viewY = testY + testH + gap
                    if (Math.Abs(viewY - (testY + testH + gap)) < thresholdY)
                    {
                        int ny = testY + testH + gap;
                        int dist = Math.Abs(viewX - testX) + Math.Abs(viewY - ny);
                        if (dist < bestDistance) { bestDistance = dist; bestLocation = new Point(testX, ny); }
                    }
                }
            }

            if (bestDistance < int.MaxValue)
            {
                view.ThumbnailLocation = bestLocation;
                this._configuration.SetThumbnailLocation(view.Title, this._activeClient.Title, view.ThumbnailLocation);
            }
        }

        private static (int X, int Y) TestViewPoints(Point[] viewPoints, Point[] testPoints, int thresholdX, int thresholdY)
        {
            // Point combinations that we need to check
            // No need to check all 4x4 combinations
            (int ViewOffset, int TestOffset)[] testOffsets =
                                {   ( 0, 3 ), ( 0, 2 ), ( 1, 2 ),
                                    ( 0, 1 ), ( 0, 0 ), ( 1, 0 ),
                                    ( 2, 1 ), ( 2, 0 ), ( 3, 0 )};

            foreach (var testOffset in testOffsets)
            {
                Point viewPoint = viewPoints[testOffset.ViewOffset];
                Point testPoint = testPoints[testOffset.TestOffset];

                int deltaX = testPoint.X - viewPoint.X;
                int deltaY = testPoint.Y - viewPoint.Y;

                if ((Math.Abs(deltaX) <= thresholdX) && (Math.Abs(deltaY) <= thresholdY))
                {
                    return (deltaX, deltaY);
                }
            }

            return (0, 0);
        }

        private void ApplyClientLayout(IntPtr clientHandle, string clientTitle)
        {
            if (!this._configuration.EnableClientLayoutTracking)
            {
                return;
            }

            // No need to apply layout for not yet logged-in clients
            if (clientTitle == ThumbnailManager.DEFAULT_CLIENT_TITLE)
            {
                return;
            }

            ClientLayout clientLayout = this._configuration.GetClientLayout(clientTitle);

            if (clientLayout == null)
            {
                return;
            }

            if (clientLayout.IsMaximized)
            {
                this._windowManager.MaximizeWindow(clientHandle);
            }
            else
            {
                this._windowManager.MoveWindow(clientHandle, clientLayout.X, clientLayout.Y, clientLayout.Width, clientLayout.Height);
            }
        }

        private void UpdateClientLayouts()
        {
            if (!this._configuration.EnableClientLayoutTracking)
            {
                return;
            }

            foreach (KeyValuePair<IntPtr, IThumbnailView> entry in this._thumbnailViews)
            {
                IThumbnailView view = entry.Value;

                // No need to save layout for not yet logged-in clients
                if (view.Title == ThumbnailManager.DEFAULT_CLIENT_TITLE)
                {
                    continue;
                }

                (int Left, int Top, int Right, int Bottom) position = this._windowManager.GetWindowPosition(view.Id);
                int width = Math.Abs(position.Right - position.Left);
                int height = Math.Abs(position.Bottom - position.Top);

                var isMaximized = this._windowManager.IsWindowMaximized(view.Id);

                if (!(isMaximized || this.IsValidWindowPosition(position.Left, position.Top, width, height)))
                {
                    continue;
                }

                this._configuration.SetClientLayout(view.Title, new ClientLayout(position.Left, position.Top, width, height, isMaximized));
            }
        }

        private void EnqueueLocationChange(IThumbnailView view)
        {
            string activeClientTitle = this._activeClient.Title;

            // 【新增逻辑】区分已登录和未登录窗口
            if (this.IsManageableThumbnail(view))
            {
                this._configuration.SetThumbnailLocation(view.Title, activeClientTitle, view.ThumbnailLocation);
            }
            else
            {
                // 1. 查找当前拖动的是第几个未登录窗口
                int draggedIndex = 0;
                foreach (var entry in this._thumbnailViews)
                {
                    if (!this.IsManageableThumbnail(entry.Value))
                    {
                        if (entry.Value.Id == view.Id) break;
                        draggedIndex++;
                    }
                }

                // 2. 反推整个未登录队列的顶部基准 Y 坐标
                int spacing = 5; // 窗口之间的垂直间距(像素)，可自行修改
                int baseY = view.ThumbnailLocation.Y - draggedIndex * (this._configuration.ThumbnailSize.Height + spacing);

                // 3. 保存基准坐标
                this._configuration.LoginThumbnailLocation = new Point(view.ThumbnailLocation.X, baseY);
            }

            lock (this._locationChangeNotificationSyncRoot)
            {
                var notification = this._enqueuedLocationChangeNotification;
                if (notification.Handle == IntPtr.Zero)
                {
                    notification.Handle = view.Id;
                    notification.Title = view.Title;
                    notification.ActiveClient = activeClientTitle;
                    notification.Location = view.ThumbnailLocation;
                    notification.Delay = ThumbnailManager.DEFAULT_LOCATION_CHANGE_NOTIFICATION_DELAY;
                    return;
                }

                // Reset the delay and exit
                if ((notification.Handle == view.Id) &&
                    (notification.ActiveClient == activeClientTitle))
                {
                    notification.Delay = ThumbnailManager.DEFAULT_LOCATION_CHANGE_NOTIFICATION_DELAY;
                    return;
                }

                this.RaiseThumbnailLocationUpdatedNotification(notification.Title);
                notification.Handle = view.Id;
                notification.Title = view.Title;
                notification.ActiveClient = activeClientTitle;
                notification.Location = view.ThumbnailLocation;
                notification.Delay = ThumbnailManager.DEFAULT_LOCATION_CHANGE_NOTIFICATION_DELAY;
            }
        }

        private bool TryDequeueLocationChange(out (IntPtr Handle, string Title, string ActiveClient, Point Location) change)
        {
            lock (this._locationChangeNotificationSyncRoot)
            {
                change = (IntPtr.Zero, null, null, Point.Empty);

                var notification = this._enqueuedLocationChangeNotification;
                if (notification.Handle == IntPtr.Zero)
                {
                    return false;
                }

                notification.Delay--;

                if (notification.Delay > 0)
                {
                    return false;
                }

                change = (notification.Handle, notification.Title, notification.ActiveClient, notification.Location);
                notification.Handle = IntPtr.Zero;
                notification.Title = null;
                notification.ActiveClient = null;
                notification.Location = Point.Empty;
                notification.Delay = -1;

                return true;
            }
        }

        private async void RaiseThumbnailLocationUpdatedNotification(string title)
        {
            if (string.IsNullOrEmpty(title) || (title == ThumbnailManager.DEFAULT_CLIENT_TITLE))
            {
                return;
            }

            await this._mediator.Send(new SaveConfiguration());
        }

        // We shouldn't manage some thumbnails (like thumbnail of the EVE client sitting on the login screen)
        // TODO Move to a service (?)
        private bool IsManageableThumbnail(IThumbnailView view)
        {
            return view.Title != ThumbnailManager.DEFAULT_CLIENT_TITLE;
        }

        // Quick sanity check that the window is not minimized
        private bool IsValidWindowPosition(int left, int top, int width, int height)
        {
            return (left > ThumbnailManager.WINDOW_POSITION_THRESHOLD_LOW) && (left < ThumbnailManager.WINDOW_POSITION_THRESHOLD_HIGH)
                    && (top > ThumbnailManager.WINDOW_POSITION_THRESHOLD_LOW) && (top < ThumbnailManager.WINDOW_POSITION_THRESHOLD_HIGH)
                    && (width > ThumbnailManager.WINDOW_SIZE_THRESHOLD) && (height > ThumbnailManager.WINDOW_SIZE_THRESHOLD);
        }
    }
}