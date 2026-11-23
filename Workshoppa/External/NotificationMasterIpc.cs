using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;

namespace Workshoppa.External;

internal sealed class NotificationMasterIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
{
    private readonly ICallGateSubscriber<string, string, string, bool> _toast =
        pluginInterface.GetIpcSubscriber<string, string, string, bool>("NotificationMasterAPI.DisplayToastNotification");
    private readonly ICallGateSubscriber<string, bool> _flash =
        pluginInterface.GetIpcSubscriber<string, bool>("NotificationMasterAPI.FlashTaskbarIcon");
    private readonly ICallGateSubscriber<bool> _isActive =
        pluginInterface.GetIpcSubscriber<bool>("NotificationMasterAPI.IsGameWindowActivated");
    private readonly string _requester = pluginInterface.InternalName;

    public bool IsAvailable => _toast.HasFunction;

    public bool TryShowToast(string title, string message) =>
        Invoke(() => _toast.HasFunction ? _toast.InvokeFunc(_requester, title, message) : null) == true;

    public bool TryFlashTaskbar() =>
        Invoke(() => _flash.HasFunction ? _flash.InvokeFunc(_requester) : null) == true;

    public bool? IsGameWindowActive() =>
        Invoke(() => _isActive.HasFunction ? _isActive.InvokeFunc() : null);

    private bool? Invoke(Func<bool?> request)
    {
        try
        {
            return request();
        }
        catch (IpcNotReadyError)
        {
            return null;
        }
        catch (Exception exception)
        {
            log.Warning(exception, "NotificationMaster could not handle a Workshoppa alert.");
            return null;
        }
    }
}
