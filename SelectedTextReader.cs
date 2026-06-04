using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NayfWindows;

/// <summary>
/// Reads the currently selected text from the foreground application using
/// UI Automation COM interop — the Windows equivalent of macOS AXUIElement.
/// Called on PTT press to detect whether the user wants text improvement.
/// </summary>
public static class SelectedTextReader
{
    /// <summary>
    /// Returns the selected text from the active application, or null if
    /// nothing is selected or accessibility access is not available.
    /// </summary>
    public static Task<string?> GetSelectedTextAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                // Use UI Automation via COM interop to read the focused element's selection
                var uiAutomation = CreateUIAutomation();
                if (uiAutomation == null) return null;

                uiAutomation.GetFocusedElement(out var focusedElement);
                if (focusedElement == null) return null;

                // Try to get the TextPattern on the focused element
                var textPatternGuid = new Guid("32EFA5D5-F0D1-4B8E-9E18-0EFA7A1DE2C0"); // UIA_TextPatternId
                focusedElement.GetCurrentPattern(10014 /* UIA_TextPatternId */, out var patternObj);

                if (patternObj is IUIAutomationTextPattern textPattern)
                {
                    textPattern.GetSelection(out var selectionRanges);
                    if (selectionRanges != null)
                    {
                        selectionRanges.GetLength(out int count);
                        if (count > 0)
                        {
                            selectionRanges.GetElement(0, out var range);
                            range.GetText(-1, out var text);
                            return string.IsNullOrWhiteSpace(text) ? null : text;
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SelectedTextReader] Failed: {ex.Message}");
                return null;
            }
        });
    }

    /// <summary>
    /// Simulates Ctrl+C to copy the current selection to the clipboard,
    /// then reads the clipboard. Used as a fallback when UI Automation
    /// doesn't return selected text — matches the Mac Cmd+C simulation.
    /// </summary>
    public static async Task<string?> GetSelectedTextViaClipboardAsync()
    {
        string? clipboardBefore = null;
        try
        {
            // Read clipboard on STA thread
            await ReadClipboardAsync(text => clipboardBefore = text);
        }
        catch { /* ignore */ }

        SimulateCtrlC();
        await Task.Delay(150);

        string? result = null;
        await ReadClipboardAsync(text =>
        {
            if (!string.IsNullOrWhiteSpace(text) && text != clipboardBefore)
                result = text;
        });
        return result;
    }

    private static Task ReadClipboardAsync(Action<string?> callback)
    {
        var tcs = new TaskCompletionSource<bool>();
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        IntPtr hData = GetClipboardData(13 /* CF_UNICODETEXT */);
                        if (hData != IntPtr.Zero)
                        {
                            IntPtr pData = GlobalLock(hData);
                            if (pData != IntPtr.Zero)
                            {
                                try
                                {
                                    callback(Marshal.PtrToStringUni(pData));
                                }
                                finally
                                {
                                    GlobalUnlock(hData);
                                }
                            }
                        }
                        else
                        {
                            callback(null);
                        }
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static void SimulateCtrlC()
    {
        keybd_event(0x11 /* VK_CONTROL */, 0, 0, 0);
        keybd_event(0x43 /* VK_C */, 0, 0, 0);
        keybd_event(0x43, 0, 2, 0); // KEYEVENTF_KEYUP
        keybd_event(0x11, 0, 2, 0);
    }

    private static IUIAutomation? CreateUIAutomation()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"));
            if (type == null) return null;
            return Activator.CreateInstance(type) as IUIAutomation;
        }
        catch { return null; }
    }

    // Minimal UI Automation COM interfaces for reading selected text
    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(/* ... */);
        void CompareRuntimeIds(/* ... */);
        void GetRootElement(out IUIAutomationElement root);
        void ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
        void ElementFromPoint(/* ... */);
        void GetFocusedElement(out IUIAutomationElement element);
        // ... other methods omitted
    }

    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId(out int[] runtimeId);
        void FindFirst(/* ... */);
        void FindAll(/* ... */);
        void FindFirstBuildCache(/* ... */);
        void FindAllBuildCache(/* ... */);
        void BuildUpdatedCache(/* ... */);
        void GetCurrentPropertyValue(int propertyId, out object retVal);
        void GetCurrentPropertyValueEx(/* ... */);
        void GetCachedPropertyValue(/* ... */);
        void GetCachedPropertyValueEx(/* ... */);
        void GetCurrentPatternAs(/* ... */);
        void GetCachedPatternAs(/* ... */);
        void GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object patternObject);
        void GetCachedPattern(/* ... */);
        // ... other methods omitted
    }

    [ComImport, Guid("32EFA5D5-F0D1-4B8E-9E18-0EFA7A1DE2C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextPattern
    {
        void RangeFromPoint(/* ... */);
        void RangeFromChild(/* ... */);
        void GetSelection(out IUIAutomationTextRangeArray ranges);
        void GetVisibleRanges(/* ... */);
        void get_DocumentRange(/* ... */);
        void get_SupportedTextSelection(/* ... */);
    }

    [ComImport, Guid("CE4AE76A-E717-4C98-81EA-47371D028EB6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRangeArray
    {
        void GetLength(out int length);
        void GetElement(int index, out IUIAutomationTextRange element);
    }

    [ComImport, Guid("A543CC6A-F4AE-494B-8239-C814481187A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRange
    {
        void Clone(/* ... */);
        void Compare(/* ... */);
        void CompareEndpoints(/* ... */);
        void ExpandToEnclosingUnit(/* ... */);
        void FindAttribute(/* ... */);
        void FindText(/* ... */);
        void GetAttributeValue(/* ... */);
        void GetBoundingRectangles(/* ... */);
        void GetEnclosingElement(/* ... */);
        void GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string text);
        // ... other methods omitted
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
