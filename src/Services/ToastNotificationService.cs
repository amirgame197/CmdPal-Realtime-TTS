using System.Security;
using System.Diagnostics;

namespace CmdPalRealtimeTTS.Services;

internal static class ToastNotificationService
{
    public static bool TryShow(string title, string message)
    {
#if CSWINRT_DISABLED
        return false;
#else
        try
        {
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml(
                $"<toast><visual><binding template=\"ToastGeneric\"><text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(message)}</text></binding></visual></toast>");
            Windows.UI.Notifications.ToastNotificationManager
                .CreateToastNotifier()
                .Show(new Windows.UI.Notifications.ToastNotification(xml));
            return true;
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Could not show Windows toast notification: {exception}");
            return false;
        }
#endif
    }
}
