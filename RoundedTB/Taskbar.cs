using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using Newtonsoft.Json;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;


namespace RoundedTB
{
    class Taskbar
    {
        /// <summary>
        /// Checks if the taskbar is centred.
        /// </summary>
        /// <returns>
        /// A bool indicating if the taskbar is centred.
        /// </returns>
        public static bool CheckIfCentred()
        {
            bool retVal;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced"))
                {
                    if (key != null)
                    {
                        int val = (int)key.GetValue("TaskbarAl");

                        if (val == 1)
                        {
                            retVal = true;
                        }
                        else
                        {
                            retVal = false;
                        }
                    }
                    else
                    {
                        retVal = false;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
            return retVal;
        }

        /// <summary>
        /// Compares two taskbars' rects to see if they've changed
        /// </summary>
        /// <returns>
        /// a bool indicating if the taskbar's, applist's, and tray's rects rects have changed.
        /// </returns>
        public static bool TaskbarRefreshRequired(Types.Taskbar currentTB, Types.Taskbar newTB, bool isDynamic)
        {
            // REMINDER: newTB will only have rect & hwnd info. Everything else will be null.


            bool taskbarRectChanged = true;
            bool appListRectChanged = true;
            bool trayRectChanged = true;

            if (
                currentTB.TaskbarRect.Left == newTB.TaskbarRect.Left &&
                currentTB.TaskbarRect.Top == newTB.TaskbarRect.Top &&
                currentTB.TaskbarRect.Right == newTB.TaskbarRect.Right &&
                currentTB.TaskbarRect.Bottom == newTB.TaskbarRect.Bottom)
            {
                taskbarRectChanged = false;
            }
            if (
                currentTB.AppListRect.Left == newTB.AppListRect.Left &&
                currentTB.AppListRect.Top == newTB.AppListRect.Top &&
                currentTB.AppListRect.Right == newTB.AppListRect.Right &&
                currentTB.AppListRect.Bottom == newTB.AppListRect.Bottom)
            {
                appListRectChanged = false;
            }
            if (
                (currentTB.TrayRect.Left + 5 >= newTB.TrayRect.Left && currentTB.TrayRect.Left - 5 <= newTB.TrayRect.Left) &&
                currentTB.TrayRect.Top == newTB.TrayRect.Top &&
                currentTB.TrayRect.Right == newTB.TrayRect.Right &&
                currentTB.TrayRect.Bottom == newTB.TrayRect.Bottom)
            {
                trayRectChanged = false;
            }

            if (isDynamic && (taskbarRectChanged || appListRectChanged || trayRectChanged))
            {
                return true;
            }
            else if (!isDynamic && taskbarRectChanged)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the rects of the three components of the taskbar from their respective handles.
        /// </summary>
        /// <returns>
        /// a partial Taskbar containing just rects and handles.
        /// </returns>
        public static Types.Taskbar GetQuickTaskbarRects(IntPtr taskbarHwnd, IntPtr trayHwnd, IntPtr appListHwnd, Types.AppListXaml appListXaml)
        {
            LocalPInvoke.GetWindowRect(taskbarHwnd, out LocalPInvoke.RECT taskbarRectCheck);
            LocalPInvoke.GetWindowRect(trayHwnd, out LocalPInvoke.RECT trayRectCheck);
            LocalPInvoke.GetWindowRect(appListHwnd, out LocalPInvoke.RECT appListRectCheck);

            LocalPInvoke.RECT? r = appListXaml?.GetWindowRect();
            if (r != null)
            {
                appListRectCheck = r.Value;
            }

            return new Types.Taskbar()
            {
                AppListXaml = appListXaml,
                TaskbarHwnd = taskbarHwnd,
                TrayHwnd = trayHwnd,
                AppListHwnd = appListHwnd,
                TaskbarRect = taskbarRectCheck,
                TrayRect = trayRectCheck,
                AppListRect = appListRectCheck
            };
        }

        /// <summary>
        /// Clears the custom window region on the specified taskbar without altering layered/opacity state.
        /// </summary>
        public static void ClearTaskbarRegion(Types.Taskbar taskbar, Types.Settings settings)
        {
            LocalPInvoke.SetWindowRgn(taskbar.TaskbarHwnd, IntPtr.Zero, true);
            if (settings.CompositionCompat)
            {
                Interaction.UpdateTranslucentTB(taskbar.TaskbarHwnd);
            }
        }

        /// <summary>
        /// Resets the specified taskbar.
        /// </summary>
        public static void ResetTaskbar(Types.Taskbar taskbar, Types.Settings settings)
        {
            LocalPInvoke.SetWindowRgn(taskbar.TaskbarHwnd, IntPtr.Zero, true);
            LocalPInvoke.SetLayeredWindowAttributes(taskbar.TaskbarHwnd, 0, 255, LocalPInvoke.LWA_ALPHA);
            int style = LocalPInvoke.GetWindowLong(taskbar.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
            if ((style & LocalPInvoke.WS_EX_LAYERED) == LocalPInvoke.WS_EX_LAYERED)
            {
                LocalPInvoke.SetWindowLong(taskbar.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE, LocalPInvoke.GetWindowLong(taskbar.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32() ^ LocalPInvoke.WS_EX_LAYERED);
            }
            style = LocalPInvoke.GetWindowLong(taskbar.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
            if ((style & LocalPInvoke.WS_EX_TRANSPARENT) == LocalPInvoke.WS_EX_TRANSPARENT)
            {
                LocalPInvoke.SetWindowLong(taskbar.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE, LocalPInvoke.GetWindowLong(taskbar.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32() ^ LocalPInvoke.WS_EX_TRANSPARENT);
            }

            if (settings.CompositionCompat)
            {
                Interaction.UpdateTranslucentTB(taskbar.TaskbarHwnd);
            }
        }

        /// <summary>
        /// Creates a basic region for a specific taskbar and applies it.
        /// </summary>
        /// <returns>
        /// a bool indicating success.
        /// </returns>
        public static bool UpdateSimpleTaskbar(Types.Taskbar taskbar, Types.Settings settings)
        {
            IntPtr region = IntPtr.Zero;
            try
            {
                // Create an effective region to be applied to the taskbar
                Types.EffectiveRegion taskbarEffectiveRegion = new Types.EffectiveRegion
                {
                    CornerRadius = Convert.ToInt32(settings.SimpleTaskbarLayout.CornerRadius * taskbar.ScaleFactor),
                    Top = Convert.ToInt32(settings.SimpleTaskbarLayout.MarginTop * taskbar.ScaleFactor),
                    Left = Convert.ToInt32(settings.SimpleTaskbarLayout.MarginLeft * taskbar.ScaleFactor),
                    Width = Convert.ToInt32(taskbar.TaskbarRect.Right - taskbar.TaskbarRect.Left - (settings.SimpleTaskbarLayout.MarginRight * taskbar.ScaleFactor)) + 1,
                    Height = Convert.ToInt32(taskbar.TaskbarRect.Bottom - taskbar.TaskbarRect.Top - (settings.SimpleTaskbarLayout.MarginBottom * taskbar.ScaleFactor)) + 1
                };

                region = LocalPInvoke.CreateRoundRectRgn(taskbarEffectiveRegion.Left, taskbarEffectiveRegion.Top, taskbarEffectiveRegion.Width, taskbarEffectiveRegion.Height, taskbarEffectiveRegion.CornerRadius, taskbarEffectiveRegion.CornerRadius);
                LocalPInvoke.SetWindowRgn(taskbar.TaskbarHwnd, region, true);
                if (settings.CompositionCompat)
                {
                    Interaction.UpdateTranslucentTB(taskbar.TaskbarHwnd);
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                LocalPInvoke.DeleteObject(region);
            }
        }

        /// <summary>
        /// Creates a dynamic region for a specific taskbar and applies it.
        /// </summary>
        /// <returns>
        /// a bool indicating success.
        /// </returns>
        public static bool UpdateDynamicTaskbar(Types.Taskbar taskbar, Types.Settings settings)
        {
            IntPtr workingRegion = IntPtr.Zero;
            IntPtr trayRegion = IntPtr.Zero;
            IntPtr widgetsRegion = IntPtr.Zero;
            IntPtr clockRegion = IntPtr.Zero;
            try
            {
                int centredDistanceFromEdge = 0;

                // Create an effective region to be applied to the taskbar for the applist
                Types.EffectiveRegion taskbarEffectiveRegion = new Types.EffectiveRegion
                {
                    CornerRadius = Convert.ToInt32(settings.DynamicAppListLayout.CornerRadius * taskbar.ScaleFactor),
                    Top = Convert.ToInt32(settings.DynamicAppListLayout.MarginTop * taskbar.ScaleFactor),
                    Left = Convert.ToInt32(settings.DynamicAppListLayout.MarginLeft * taskbar.ScaleFactor),
                    Width = Convert.ToInt32(taskbar.TaskbarRect.Right - taskbar.TaskbarRect.Left - (settings.DynamicAppListLayout.MarginRight * taskbar.ScaleFactor)) + 1,
                    Height = Convert.ToInt32(taskbar.TaskbarRect.Bottom - taskbar.TaskbarRect.Top - (settings.DynamicAppListLayout.MarginBottom * taskbar.ScaleFactor)) + 1
                };

                // Create an effective region to be applied to the taskbar for the applist
                Types.EffectiveRegion centredEffectiveRegion = new Types.EffectiveRegion
                {
                    CornerRadius = Convert.ToInt32(settings.DynamicAppListLayout.CornerRadius * taskbar.ScaleFactor),
                    Top = Convert.ToInt32(settings.DynamicAppListLayout.MarginTop * taskbar.ScaleFactor),
                    Left = Convert.ToInt32(settings.DynamicAppListLayout.MarginRight * taskbar.ScaleFactor) - 1,
                    Width = Convert.ToInt32(taskbar.TaskbarRect.Right - taskbar.TaskbarRect.Left - (settings.DynamicAppListLayout.MarginRight * taskbar.ScaleFactor)) + 1,
                    Height = Convert.ToInt32(taskbar.TaskbarRect.Bottom - taskbar.TaskbarRect.Top - (settings.DynamicAppListLayout.MarginBottom * taskbar.ScaleFactor)) + 1
                };

                // Create an effective region to be applied to the taskbar for the tray
                Types.EffectiveRegion trayEffectiveRegion = new Types.EffectiveRegion
                {
                    CornerRadius = Convert.ToInt32(settings.DynamicTrayLayout.CornerRadius * taskbar.ScaleFactor),
                    Top = Convert.ToInt32(settings.DynamicTrayLayout.MarginTop * taskbar.ScaleFactor),
                    Left = Convert.ToInt32((settings.DynamicTrayLayout.MarginLeft * taskbar.ScaleFactor) - (3 * taskbar.ScaleFactor)), // Add extra margin for taskbar left as there's no "padding" provided by Windows and always looks weird as soon as you trim it otherwise.
                    Width = Convert.ToInt32(taskbar.TaskbarRect.Right - taskbar.TaskbarRect.Left - (settings.DynamicTrayLayout.MarginRight * taskbar.ScaleFactor)) + 1,
                    Height = Convert.ToInt32(taskbar.TaskbarRect.Bottom - taskbar.TaskbarRect.Top - (settings.DynamicTrayLayout.MarginBottom * taskbar.ScaleFactor)) + 1
                };

                Types.EffectiveRegion widgetsEffectiveRegion = new Types.EffectiveRegion
                {
                    CornerRadius = Convert.ToInt32(settings.DynamicWidgetsLayout.CornerRadius * taskbar.ScaleFactor),
                    Top = Convert.ToInt32(settings.DynamicWidgetsLayout.MarginTop * taskbar.ScaleFactor),
                    Left = Convert.ToInt32(settings.DynamicWidgetsLayout.MarginLeft * taskbar.ScaleFactor),
                    Width = Convert.ToInt32(settings.WidgetsWidth * taskbar.ScaleFactor - (settings.DynamicWidgetsLayout.MarginRight * taskbar.ScaleFactor)) + 1,
                    Height = Convert.ToInt32(taskbar.TaskbarRect.Bottom - taskbar.TaskbarRect.Top - (settings.DynamicWidgetsLayout.MarginBottom * taskbar.ScaleFactor)) + 1
                };


                Types.EffectiveRegion secondaryClockRegion = new Types.EffectiveRegion
                {
                    CornerRadius = Convert.ToInt32(settings.DynamicSecondaryClockLayout.CornerRadius * taskbar.ScaleFactor),
                    Top = Convert.ToInt32(settings.DynamicSecondaryClockLayout.MarginTop * taskbar.ScaleFactor),
                    Left = (taskbar.TaskbarRect.Right - taskbar.TaskbarRect.Left) - settings.ClockWidth - Convert.ToInt32(settings.DynamicSecondaryClockLayout.MarginLeft * taskbar.ScaleFactor),
                    Width = Convert.ToInt32(settings.ClockWidth * taskbar.ScaleFactor - (settings.DynamicSecondaryClockLayout.MarginRight * taskbar.ScaleFactor)) + 1,
                    Height = Convert.ToInt32(taskbar.TaskbarRect.Bottom - taskbar.TaskbarRect.Top - (settings.DynamicSecondaryClockLayout.MarginBottom * taskbar.ScaleFactor)) + 1
                };

                centredDistanceFromEdge = taskbar.TaskbarRect.Right - taskbar.AppListRect.Right - Convert.ToInt32(2 * taskbar.ScaleFactor);

                // If on Windows 10, add an extra 20 logical pixels for the grabhandle
                if (!settings.IsWindows11)
                {
                    centredDistanceFromEdge -= Convert.ToInt32(20 * taskbar.ScaleFactor);
                }

                // Create region for if the taskbar is centred by take the right-to-right distance (centredDistanceFromEdge) off from both sides, as well as the margin
                if (settings.IsCentred)
                {
                    workingRegion = LocalPInvoke.CreateRoundRectRgn(
                        centredDistanceFromEdge + centredEffectiveRegion.Left,
                        centredEffectiveRegion.Top,
                        centredEffectiveRegion.Width - centredDistanceFromEdge,
                        centredEffectiveRegion.Height,
                        centredEffectiveRegion.CornerRadius,
                        centredEffectiveRegion.CornerRadius
                        );
                }

                // Create a region for if the taskbar is left-aligned, right-to-right distance (centredDistanceFromEdge) off from the right-hand side, as well as the margin
                else
                {

                    workingRegion = LocalPInvoke.CreateRoundRectRgn(
                        taskbarEffectiveRegion.Left,
                        taskbarEffectiveRegion.Top,
                        taskbarEffectiveRegion.Width - centredDistanceFromEdge,
                        taskbarEffectiveRegion.Height,
                        taskbarEffectiveRegion.CornerRadius,
                        taskbarEffectiveRegion.CornerRadius
                        );
                }

                if (settings.ShowSecondaryClock && taskbar.IsSecondary)
                {
                    clockRegion = LocalPInvoke.CreateRoundRectRgn(
                        secondaryClockRegion.Left,
                        secondaryClockRegion.Top,
                        secondaryClockRegion.Width + secondaryClockRegion.Left,
                        secondaryClockRegion.Height,
                        secondaryClockRegion.CornerRadius,
                        secondaryClockRegion.CornerRadius
                        );
                    LocalPInvoke.CombineRgn(workingRegion, clockRegion, workingRegion, 2);
                }

                // If the user has it enabled and the tray handle isn't null, create a region for the system tray and merge it with the taskbar region
                if (settings.ShowTray && taskbar.TrayHwnd != IntPtr.Zero)
                {
                    trayRegion = LocalPInvoke.CreateRoundRectRgn(
                        (taskbar.TrayRect.Left - taskbar.TaskbarRect.Left) - trayEffectiveRegion.Left,
                        trayEffectiveRegion.Top,
                        trayEffectiveRegion.Width,
                        trayEffectiveRegion.Height,
                        trayEffectiveRegion.CornerRadius,
                        trayEffectiveRegion.CornerRadius
                        );

                    LocalPInvoke.CombineRgn(workingRegion, trayRegion, workingRegion, 2);
                }

                if (settings.ShowWidgets)
                {
                    widgetsRegion = LocalPInvoke.CreateRoundRectRgn(
                        widgetsEffectiveRegion.Left,
                        widgetsEffectiveRegion.Top,
                        widgetsEffectiveRegion.Width,
                        widgetsEffectiveRegion.Height,
                        widgetsEffectiveRegion.CornerRadius,
                        widgetsEffectiveRegion.CornerRadius
                        );
                    LocalPInvoke.CombineRgn(workingRegion, widgetsRegion, workingRegion, 2);
                }


                // Apply the final region to the taskbar
                LocalPInvoke.SetWindowRgn(taskbar.TaskbarHwnd, workingRegion, true);
                if (settings.CompositionCompat)
                {
                    Interaction.UpdateTranslucentTB(taskbar.TaskbarHwnd);
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                LocalPInvoke.DeleteObject(clockRegion);
                LocalPInvoke.DeleteObject(widgetsRegion);
                LocalPInvoke.DeleteObject(trayRegion);
                LocalPInvoke.DeleteObject(workingRegion);
            }

        }

        /// <summary>
        /// Checks if there are any new taskbars, or if any taskbars are no longer present.
        /// </summary>
        /// <returns>
        /// a bool indicating success.
        /// </returns>
        public static bool TaskbarCountOrHandleChanged(int taskbarCount, IntPtr mainTaskbarHandle)
        {
            List<IntPtr> currentTaskbars = new List<IntPtr>();
            bool otherTaskbarsExist = true;
            IntPtr hwndPrevious = IntPtr.Zero;
            currentTaskbars.Add(LocalPInvoke.FindWindowExA(IntPtr.Zero, hwndPrevious, "Shell_TrayWnd", null));

            if (currentTaskbars[0] == IntPtr.Zero)
            {
                return false;
            }

            if (currentTaskbars[0] != mainTaskbarHandle)
            {
                return true;
            }

            while (otherTaskbarsExist)
            {
                IntPtr hwndCurrent = LocalPInvoke.FindWindowExA(IntPtr.Zero, hwndPrevious, "Shell_SecondaryTrayWnd", null);
                hwndPrevious = hwndCurrent;

                if (hwndCurrent == IntPtr.Zero)
                {
                    otherTaskbarsExist = false;
                }
                else
                {
                    currentTaskbars.Add(hwndCurrent);
                }
            }
            if (currentTaskbars.Count != taskbarCount)
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if the provided update is valid.
        /// </summary>
        /// <returns>
        /// A bool indicating if the update is valid.
        /// </returns>
        public static bool CheckDynamicUpdateIsValid(Types.Taskbar currentTB, Types.Taskbar newTB)
        {
            // REMINDER: newTB will only have rect & hwnd info. Everything else will be null.

            // Check if either of the supplied taskbars are null
            if (currentTB == null || newTB == null)
            {
                return false;
            }

            // Check if the taskbar handles are different
            if (currentTB.TaskbarHwnd != newTB.TaskbarHwnd)
            {
                return false;
            }

            // Get width of app list. Not strictly necessary as the applist is always measured from the left but doing so just in case
            int newAppListWidth = newTB.AppListRect.Right - newTB.AppListRect.Left;
            int currentAppListWidth = currentTB.AppListRect.Right - currentTB.AppListRect.Left;

            if (newTB.AppListRect.Right >= newTB.TrayRect.Left && newTB.TrayRect.Left != 0)
            {
                return false;
            }

            if (newAppListWidth == newTB.TrayRect.Left && newTB.TrayRect.Left != 0)
            {
                return false;
            }

            if (newAppListWidth <= 20 * currentTB.ScaleFactor && newAppListWidth != 0)
            {
                return false;
            }

            if (newAppListWidth >= newTB.TaskbarRect.Right - newTB.TaskbarRect.Left && newAppListWidth != 0)
            {
                return false;
            }

            Debug.WriteLine($"Old width: {currentAppListWidth}\nNew width: {newAppListWidth}");
            return true;
        }


        /// <summary>Get AppList handle for win23h2 and later. </summary>
        public static Types.AppListXaml GetAppListSince23H2(IntPtr hwndTaskbarMain)
        {
            return new Types.AppListXaml(hwndTaskbarMain);
        }


        /// <summary>
        /// Collects information on any currently-present taskbars.
        /// </summary>
        /// <returns>
        /// A list of taskbars populated with information about their size, handles etc.
        /// </returns>
        public static List<Types.Taskbar> GenerateTaskbarInfo(bool isWindows11)
        {
            List<Types.Taskbar> retVal = new List<Types.Taskbar>();

            IntPtr hwndMain = LocalPInvoke.FindWindowExA(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null); // Find main taskbar
            LocalPInvoke.GetWindowRect(hwndMain, out LocalPInvoke.RECT rectMain); // Get the RECT of the main taskbar
            IntPtr hrgnMain = IntPtr.Zero; // Set recovery region to IntPtr.Zero
            IntPtr hwndTray = LocalPInvoke.FindWindowExA(hwndMain, IntPtr.Zero, "TrayNotifyWnd", null); // Get handle to the main taskbar's tray
            LocalPInvoke.GetWindowRect(hwndTray, out LocalPInvoke.RECT rectTray); // Get the RECT for the main taskbar's tray
            IntPtr hwndAppList = LocalPInvoke.FindWindowExA(LocalPInvoke.FindWindowExA(hwndMain, IntPtr.Zero, "ReBarWindow32", null), IntPtr.Zero, "MSTaskSwWClass", null); // Get the handle to the main taskbar's app list
            LocalPInvoke.GetWindowRect(hwndAppList, out LocalPInvoke.RECT rectAppList);// Get the RECT for the main taskbar's app list
            Types.AppListXaml appList = GetAppListSince23H2(hwndMain);

            retVal.Add(new Types.Taskbar
            {
                AppListXaml = appList,
                TaskbarHwnd = hwndMain,
                TrayHwnd = hwndTray,
                AppListHwnd = hwndAppList,
                TaskbarRect = rectMain,
                TrayRect = rectTray,
                AppListRect = rectAppList,
                RecoveryHrgn = hrgnMain,
                ScaleFactor = Convert.ToDouble(LocalPInvoke.GetDpiForWindow(hwndMain)) / 96.00,
                TaskbarRes = $"{rectMain.Right - rectMain.Left} x {rectMain.Bottom - rectMain.Top}",
                Ignored = false,
                IsSecondary = false,
            });
            int style = LocalPInvoke.GetWindowLong(hwndMain, LocalPInvoke.GWL_EXSTYLE).ToInt32();
            if ((style & LocalPInvoke.WS_EX_LAYERED) != LocalPInvoke.WS_EX_LAYERED)
            {
                LocalPInvoke.SetWindowLong(hwndMain, LocalPInvoke.GWL_EXSTYLE, LocalPInvoke.GetWindowLong(hwndMain, LocalPInvoke.GWL_EXSTYLE).ToInt32() ^ LocalPInvoke.WS_EX_LAYERED);
                LocalPInvoke.SetLayeredWindowAttributes(hwndMain, 0, 255, LocalPInvoke.LWA_ALPHA);
            }




            bool i = true;
            IntPtr hwndPrevious = IntPtr.Zero;
            while (i)
            {
                IntPtr hwndCurrent = LocalPInvoke.FindWindowExA(IntPtr.Zero, hwndPrevious, "Shell_SecondaryTrayWnd", null);
                hwndPrevious = hwndCurrent;

                if (hwndCurrent == IntPtr.Zero)
                {
                    i = false;
                }
                else
                {
                    LocalPInvoke.GetWindowRect(hwndCurrent, out LocalPInvoke.RECT rectCurrent);
                    LocalPInvoke.GetWindowRgn(hwndCurrent, out IntPtr hrgnCurrent);
                    IntPtr hwndSecTray = IntPtr.Zero;
                    if (isWindows11)
                    {
                        IntPtr imd = LocalPInvoke.FindWindowExA(hwndCurrent, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null);
                        hwndSecTray = LocalPInvoke.FindWindowExA(hwndCurrent, imd, "Windows.UI.Composition.DesktopWindowContentBridge", null);
                    }
                    else
                    {
                        hwndSecTray = LocalPInvoke.FindWindowExA(hwndCurrent, IntPtr.Zero, "TrayNotifyWnd", null); // Get handle to this secondary taskbar's tray
                    }
                    LocalPInvoke.GetWindowRect(hwndSecTray, out LocalPInvoke.RECT rectSecTray); // Get the RECT for this secondary taskbar's tray
                    IntPtr hwndWorkerW = LocalPInvoke.FindWindowExA(hwndCurrent, IntPtr.Zero, "WorkerW", null);
                    IntPtr hwndSecAppList = IntPtr.Zero;
                    // windows 11 22H2 has multiple WorkerW handles.
                    while (hwndWorkerW != IntPtr.Zero)
                    {
                        hwndSecAppList = LocalPInvoke.FindWindowExA(hwndWorkerW, IntPtr.Zero, "MSTaskListWClass", null); // Get the handle to the main taskbar's app list
                        if (hwndSecAppList != IntPtr.Zero)
                        {
                            break;
                        }
                        hwndWorkerW = LocalPInvoke.FindWindowExA(hwndCurrent, hwndWorkerW, "WorkerW", null);
                    }
                    LocalPInvoke.GetWindowRect(hwndSecAppList, out LocalPInvoke.RECT rectSecAppList);// Get the RECT for this secondary taskbar's app list
                    Types.AppListXaml appListSec = GetAppListSince23H2(hwndCurrent);
                    retVal.Add(new Types.Taskbar
                    {
                        AppListXaml = appListSec,
                        TaskbarHwnd = hwndCurrent,
                        TrayHwnd = hwndSecTray,
                        AppListHwnd = hwndSecAppList,
                        TaskbarRect = rectCurrent,
                        TrayRect = rectSecTray,
                        AppListRect = rectSecAppList,
                        RecoveryHrgn = hrgnCurrent,
                        ScaleFactor = Convert.ToDouble(LocalPInvoke.GetDpiForWindow(hwndCurrent)) / 96.00,
                        TaskbarRes = $"{rectCurrent.Right - rectCurrent.Left} x {rectCurrent.Bottom - rectCurrent.Top}",
                        Ignored = false,
                        IsSecondary = true,
                    });
                    style = LocalPInvoke.GetWindowLong(hwndCurrent, LocalPInvoke.GWL_EXSTYLE).ToInt32();
                    if ((style & LocalPInvoke.WS_EX_LAYERED) != LocalPInvoke.WS_EX_LAYERED)
                    {
                        LocalPInvoke.SetWindowLong(hwndCurrent, LocalPInvoke.GWL_EXSTYLE, LocalPInvoke.GetWindowLong(hwndCurrent, LocalPInvoke.GWL_EXSTYLE).ToInt32() ^ LocalPInvoke.WS_EX_LAYERED);
                        LocalPInvoke.SetLayeredWindowAttributes(hwndCurrent, 0, 255, LocalPInvoke.LWA_ALPHA);
                    }
                }
            }

            //foreach (var tb in retVal)
            //{
            //    TaskbarShouldBeFilled(tb.TaskbarHwnd);
            //}
            return retVal;
        }

        private static IntPtr cachedFgHwnd;
        private static uint cachedFgPid;
        private static bool cachedFgIsStartMenu;

        /// <summary>
        /// Checks whether the Windows Start menu (or Start search) is currently open on the taskbar's monitor.
        /// </summary>
        public static bool IsStartMenuOpen(IntPtr taskbarHwnd)
        {
            IntPtr fgHwnd = LocalPInvoke.GetForegroundWindow();
            if (fgHwnd == IntPtr.Zero || !LocalPInvoke.IsWindowVisible(fgHwnd))
            {
                return false;
            }
            if (LocalPInvoke.MonitorFromWindow(fgHwnd, 2) != LocalPInvoke.MonitorFromWindow(taskbarHwnd, 2))
            {
                return false;
            }
            LocalPInvoke.DwmGetWindowAttribute(fgHwnd, LocalPInvoke.DWMWINDOWATTRIBUTE.Cloaked, out bool isCloaked, 0x4);
            if (isCloaked)
            {
                return false;
            }

            LocalPInvoke.GetWindowThreadProcessId(fgHwnd, out uint pid);
            if (pid == 0)
            {
                return false;
            }
            if (fgHwnd != cachedFgHwnd || pid != cachedFgPid)
            {
                cachedFgHwnd = fgHwnd;
                cachedFgPid = pid;
                cachedFgIsStartMenu = IsStartHostProcess(pid);
            }
            return cachedFgIsStartMenu;
        }

        private static bool IsStartHostProcess(uint pid)
        {
            IntPtr hProcess = LocalPInvoke.OpenProcess(LocalPInvoke.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                StringBuilder path = new StringBuilder(260);
                int size = path.Capacity;
                if (!LocalPInvoke.QueryFullProcessImageName(hProcess, 0, path, ref size))
                {
                    return false;
                }
                string exeName = System.IO.Path.GetFileName(path.ToString());
                return string.Equals(exeName, "StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(exeName, "SearchHost.exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(exeName, "SearchApp.exe", StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                LocalPInvoke.CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// Checks whether the Windows 11 Alt+Tab task switcher is currently open.
        /// </summary>
        public static bool IsTaskSwitcherOpen(Types.Settings settings)
        {
            if (!settings.FillOnTaskSwitch)
            {
                return false;
            }
            IntPtr topHwnd = LocalPInvoke.WindowFromPoint(new LocalPInvoke.POINT() { x = 0, y = 0 });
            StringBuilder windowClass = new StringBuilder(1024);
            try
            {
                LocalPInvoke.GetClassName(topHwnd, windowClass, 1024);
                return windowClass.ToString() == "XamlExplorerHostIslandWindow";
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsMaximizedWindowOnMonitor(IntPtr windowHwnd, IntPtr taskbarHwnd, IntPtr taskbarMonitor)
        {
            if (windowHwnd == taskbarHwnd || !LocalPInvoke.IsWindowVisible(windowHwnd))
            {
                return false;
            }
            if (LocalPInvoke.MonitorFromWindow(windowHwnd, 2) != taskbarMonitor)
            {
                return false;
            }
            LocalPInvoke.DwmGetWindowAttribute(windowHwnd, LocalPInvoke.DWMWINDOWATTRIBUTE.Cloaked, out bool isCloaked, 0x4);
            if (isCloaked)
            {
                return false;
            }
            LocalPInvoke.WINDOWPLACEMENT lpwndpl = LocalPInvoke.WINDOWPLACEMENT.Default;
            return LocalPInvoke.GetWindowPlacement(windowHwnd, ref lpwndpl)
                && lpwndpl.ShowCmd == LocalPInvoke.ShowWindowCommands.ShowMaximized;
        }

        /// <summary>
        /// Checks whether there is a visible, uncloaked maximised window on the same monitor as the taskbar.
        /// </summary>
        public static bool HasMaximizedWindow(IntPtr taskbarHwnd)
        {
            IntPtr taskbarMonitor = LocalPInvoke.MonitorFromWindow(taskbarHwnd, 2);
            foreach (IntPtr windowHwnd in Interaction.GetTopLevelWindows())
            {
                if (IsMaximizedWindowOnMonitor(windowHwnd, taskbarHwnd, taskbarMonitor))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Expands any maximised windows on the taskbar's monitor whose frame stops short of targetWorkArea.
        /// </summary>
        public static void ExpandMaximizedWindows(IntPtr taskbarHwnd, LocalPInvoke.RECT targetWorkArea)
        {
            IntPtr taskbarMonitor = LocalPInvoke.MonitorFromWindow(taskbarHwnd, 2);
            foreach (IntPtr windowHwnd in Interaction.GetTopLevelWindows())
            {
                if (!IsMaximizedWindowOnMonitor(windowHwnd, taskbarHwnd, taskbarMonitor))
                {
                    continue;
                }
                if (!LocalPInvoke.GetWindowRect(windowHwnd, out LocalPInvoke.RECT winRect)
                    || winRect.Right <= winRect.Left || winRect.Bottom <= winRect.Top)
                {
                    continue;
                }
                LocalPInvoke.RECT extBounds;
                if (LocalPInvoke.DwmGetWindowAttribute(windowHwnd, LocalPInvoke.DWMWINDOWATTRIBUTE.ExtendedFrameBounds,
                    out extBounds, Marshal.SizeOf<LocalPInvoke.RECT>()) != 0)
                {
                    extBounds = winRect;
                }

                int leftGap = Math.Max(0, extBounds.Left - targetWorkArea.Left);
                int topGap = Math.Max(0, extBounds.Top - targetWorkArea.Top);
                int rightGap = Math.Max(0, targetWorkArea.Right - extBounds.Right);
                int bottomGap = Math.Max(0, targetWorkArea.Bottom - extBounds.Bottom);
                if (leftGap == 0 && topGap == 0 && rightGap == 0 && bottomGap == 0)
                {
                    continue;
                }

                // Skip windows that aren't already snapped to the other three monitor edges (e.g. mid-animation).
                int alignedEdges = 0;
                if (Math.Abs(extBounds.Left - targetWorkArea.Left) <= 2) alignedEdges++;
                if (Math.Abs(extBounds.Top - targetWorkArea.Top) <= 2) alignedEdges++;
                if (Math.Abs(extBounds.Right - targetWorkArea.Right) <= 2) alignedEdges++;
                if (Math.Abs(extBounds.Bottom - targetWorkArea.Bottom) <= 2) alignedEdges++;
                if (alignedEdges < 3)
                {
                    continue;
                }

                LocalPInvoke.SetWindowPosFlags flags = LocalPInvoke.SetWindowPosFlags.IgnoreZOrder | LocalPInvoke.SetWindowPosFlags.DoNotActivate;
                if (leftGap == 0 && topGap == 0)
                {
                    flags |= LocalPInvoke.SetWindowPosFlags.IgnoreMove;
                }

                LocalPInvoke.SetWindowPos(
                    windowHwnd,
                    IntPtr.Zero,
                    winRect.Left - leftGap,
                    winRect.Top - topGap,
                    (winRect.Right - winRect.Left) + leftGap + rightGap,
                    (winRect.Bottom - winRect.Top) + topGap + bottomGap,
                    flags);
            }
        }

        public static bool RectsEqual(LocalPInvoke.RECT a, LocalPInvoke.RECT b)
        {
            return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
        }

        /// <summary>
        /// Computes a monitor's normal work area with space reserved for the given taskbar.
        /// </summary>
        public static LocalPInvoke.RECT GetRestoredWorkArea(LocalPInvoke.RECT monitorRect, LocalPInvoke.RECT taskbarRect)
        {
            LocalPInvoke.RECT workArea = monitorRect;
            if (taskbarRect.Right <= taskbarRect.Left || taskbarRect.Bottom <= taskbarRect.Top)
            {
                return workArea;
            }
            bool horizontal = (taskbarRect.Right - taskbarRect.Left) >= (taskbarRect.Bottom - taskbarRect.Top);
            if (horizontal && taskbarRect.Top >= (monitorRect.Top + monitorRect.Bottom) / 2)
            {
                workArea.Bottom -= taskbarRect.Bottom - taskbarRect.Top;
            }
            else if (horizontal)
            {
                workArea.Top += taskbarRect.Bottom - taskbarRect.Top;
            }
            else if (taskbarRect.Left >= (monitorRect.Left + monitorRect.Right) / 2)
            {
                workArea.Right -= taskbarRect.Right - taskbarRect.Left;
            }
            else
            {
                workArea.Left += taskbarRect.Right - taskbarRect.Left;
            }
            return workArea;
        }

        /// <summary>
        /// Sets the monitor's work area if it differs from targetWorkArea. Returns true if updated.
        /// </summary>
        public static bool SetMonitorWorkArea(IntPtr monitorHandle, LocalPInvoke.RECT targetWorkArea, bool broadcast = true)
        {
            MonitorStuff.MONITORINFO mi = new MonitorStuff.MONITORINFO
            {
                cbSize = (uint)Marshal.SizeOf<MonitorStuff.MONITORINFO>(),
            };
            if (MonitorStuff.GetMonitorInfo(monitorHandle, ref mi) && RectsEqual(mi.rcWork, targetWorkArea))
            {
                return false;
            }
            Interaction.SetWorkspace(targetWorkArea, broadcast);
            return true;
        }

        /// <summary>
        /// Clears Windows' built-in taskbar auto-hide only if enabled, so Explorer does not reset custom work areas.
        /// </summary>
        public static void EnsureAlwaysOnTopState(IntPtr hwnd)
        {
            LocalPInvoke.APPBARDATA msgData = new LocalPInvoke.APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<LocalPInvoke.APPBARDATA>(),
                hWnd = hwnd,
            };
            int state = LocalPInvoke.SHAppBarMessage(LocalPInvoke.ABM.GetState, ref msgData).ToInt32();
            if ((state & LocalPInvoke.ABS.Autohide) != 0)
            {
                SetTaskbarState(LocalPInvoke.AppBarStates.AlwaysOnTop, hwnd);
            }
        }

        /// <summary>
        /// Checks if the given taskbar should be filled to the edge of the screen.
        /// </summary>
        /// <returns>
        /// A bool indicating whether or not the taskbar needs to be filled.
        /// </returns>
        public static bool TaskbarShouldBeFilled(IntPtr taskbarHwnd, Types.Settings settings)
        {
            return settings.FillOnMaximise && (IsTaskSwitcherOpen(settings) || HasMaximizedWindow(taskbarHwnd));
        }

        /// <summary>
        /// Sets the appbar properties of the taskbar.
        /// </summary>
        public static void SetTaskbarState(LocalPInvoke.AppBarStates option, IntPtr hwnd)
        {
            LocalPInvoke.APPBARDATA msgData = new LocalPInvoke.APPBARDATA();
            msgData.cbSize = (uint)Marshal.SizeOf(msgData);
            msgData.hWnd = hwnd;
            msgData.lParam = (int)option;
            LocalPInvoke.SHAppBarMessage(LocalPInvoke.ABM.SetState, ref msgData);
        }


    }
}
