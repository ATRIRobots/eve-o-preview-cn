using EveOPreview.Services.Interop;
using System;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace EveOPreview.Services.Implementation
{
    public class WindowManager : IWindowManager
    {
        #region Private constants
        private const int WINDOW_SIZE_THRESHOLD = 300;
        #endregion
        
        public WindowManager()
        {
            // Composition is always enabled for Windows 8+
            this.IsCompositionEnabled = 
                ((Environment.OSVersion.Version.Major == 6) && (Environment.OSVersion.Version.Minor >= 2)) // Win 8 and Win 8.1
                || (Environment.OSVersion.Version.Major >= 10) // Win 10
                || DwmNativeMethods.DwmIsCompositionEnabled(); // In case of Win 7 an API call is requiredWin 7
        }

        public bool IsCompositionEnabled { get; }
        
        /// <summary>
        /// Track the state if the manager is part way through switching a client at the moment.
        /// </summary>
        public bool IsCurrentlySwitching { get; set; } = false;

        public IntPtr GetForegroundWindowHandle()
        {
            return User32NativeMethods.GetForegroundWindow();
        }

        public void TrySendGiveFocusViaFpsLimiterNamedPipe(IntPtr handle)
        {
            if (!Global.IsFpsThrottlingEnabled)
            {
                return;
            }
            
            // Fire and forget
            Task.Run(() =>
            {
                try
                {
                    var fpsLimiterNamedPipeForThisClient = $"FpsLimiter_{handle}";

                    using (var client = new NamedPipeClientStream(".", fpsLimiterNamedPipeForThisClient,
                               PipeDirection.InOut, PipeOptions.None))
                    {
                        client.Connect(100);
                        using (var writer = new BinaryWriter(client))
                        {
                            writer.Write((byte)0xA3);
                            writer.Write((byte)0xB1);
                            writer.Flush();
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Just be silent and don't block anything else.
                }
            });
        }

        public void MakeApiCallsToSetForegroundAndFocus(IntPtr handle)
        {
            try
            {
                User32NativeMethods.SwitchToThisWindow(handle, false);
                User32NativeMethods.SetFocus(handle);
            }
            catch
            {
                User32NativeMethods.SetForegroundWindow(handle);
                User32NativeMethods.SetFocus(handle);
            }
        }

        public void ActivateWindow(IntPtr handle)
        {
            TrySendGiveFocusViaFpsLimiterNamedPipe(handle);

            MakeApiCallsToSetForegroundAndFocus(handle);

            int style = User32NativeMethods.GetWindowLong(handle, InteropConstants.GWL_STYLE);

            if ((style & InteropConstants.WS_MINIMIZE) == InteropConstants.WS_MINIMIZE)
            {
                User32NativeMethods.ShowWindowAsync(handle, InteropConstants.SW_RESTORE);
            }
        }

        // Cache the struct size to avoid repeated Marshal.SizeOf calls
        private static readonly int _windowPlacementSize = Marshal.SizeOf(typeof(WINDOWPLACEMENT));

        public void MinimizeWindow(IntPtr handle, bool enableAnimation)
        {
            if (enableAnimation)
            {
                User32NativeMethods.SendMessage(handle, InteropConstants.WM_SYSCOMMAND, InteropConstants.SC_MINIMIZE, 0);
            }
            else
            {
                WINDOWPLACEMENT param = new WINDOWPLACEMENT();
                param.length = _windowPlacementSize;
                User32NativeMethods.GetWindowPlacement(handle, ref param);
                param.showCmd = WINDOWPLACEMENT.SW_MINIMIZE;
                User32NativeMethods.SetWindowPlacement(handle, ref param);
            }
        }

        public void MoveWindow(IntPtr handle, int left, int top, int width, int height)
        {
            User32NativeMethods.MoveWindow(handle, left, top, width, height, true);
        }

        public void MaximizeWindow(IntPtr handle)
        {
            User32NativeMethods.ShowWindowAsync(handle, InteropConstants.SW_SHOWMAXIMIZED);
        }

        public (int Left, int Top, int Right, int Bottom) GetWindowPosition(IntPtr handle)
        {
            User32NativeMethods.GetWindowRect(handle, out RECT windowRectangle);

            return (windowRectangle.Left, windowRectangle.Top, windowRectangle.Right, windowRectangle.Bottom);
        }

        public bool IsWindowMaximized(IntPtr handle)
        {
            return User32NativeMethods.IsZoomed(handle);
        }

        public bool IsWindowMinimized(IntPtr handle)
        {
            return User32NativeMethods.IsIconic(handle);
        }

        public IDwmThumbnail GetLiveThumbnail(IntPtr destination, IntPtr source)
        {
            IDwmThumbnail thumbnail = new DwmThumbnail(this);
            thumbnail.Register(destination, source);

            return thumbnail;
        }

        public Image GetStaticThumbnail(IntPtr source)
        {
            IntPtr sourceContext = IntPtr.Zero;
            IntPtr destContext = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero;

            try
            {
                sourceContext = User32NativeMethods.GetDC(source);

                User32NativeMethods.GetClientRect(source, out RECT windowRect);

                var width = windowRect.Right - windowRect.Left;
                var height = windowRect.Bottom - windowRect.Top;

                // Check if there is anything to make thumbnail of
                if ((width < WINDOW_SIZE_THRESHOLD) || (height < WINDOW_SIZE_THRESHOLD))
                {
                    return null;
                }

                destContext = Gdi32NativeMethods.CreateCompatibleDC(sourceContext);
                bitmap = Gdi32NativeMethods.CreateCompatibleBitmap(sourceContext, width, height);

                var oldBitmap = Gdi32NativeMethods.SelectObject(destContext, bitmap);
                Gdi32NativeMethods.BitBlt(destContext, 0, 0, width, height, sourceContext, 0, 0, Gdi32NativeMethods.SRCCOPY);
                Gdi32NativeMethods.SelectObject(destContext, oldBitmap);

                Image image = Image.FromHbitmap(bitmap);
                return image;
            }
            finally
            {
                // Clean up GDI resources in reverse order of creation
                if (bitmap != IntPtr.Zero)
                {
                    Gdi32NativeMethods.DeleteObject(bitmap);
                }
                if (destContext != IntPtr.Zero)
                {
                    Gdi32NativeMethods.DeleteDC(destContext);
                }
                if (sourceContext != IntPtr.Zero)
                {
                    User32NativeMethods.ReleaseDC(source, sourceContext);
                }
            }
        }
    }
}
