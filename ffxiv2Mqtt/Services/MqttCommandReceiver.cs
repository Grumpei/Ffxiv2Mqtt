using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Ffxiv2Mqtt.Services;

/// <summary>
/// Subscribes to MQTT and forwards payloads as in-game commands.
/// Topic: ffxiv/Command/Chat  (or ffxiv/{ClientId}/Command/Chat)
/// </summary>
public sealed class MqttCommandReceiver : IDisposable
{
    // Signature for ProcessChatBox - stable across patches
    // Same sig used by SomethingNeedDoing, QoLBar, etc.
    private delegate void ProcessChatBoxDelegate(nint uiModule, nint message, nint unused, byte a4);
    private readonly ProcessChatBoxDelegate? processChatBox;
    private readonly nint uiModulePtr;

    private readonly IMqttClientWrapper mqttClient;
    private readonly ICommandManager    commandManager;
    private readonly IChatGui           chatGui;
    private readonly IClientState       clientState;
    private readonly IFramework         framework;
    private readonly IPluginLog         log;
    private readonly Configuration      config;
    private readonly ISigScanner        sigScanner;

    private string subscribedTopic = string.Empty;

    public MqttCommandReceiver(
        IMqttClientWrapper mqttClient,
        ICommandManager    commandManager,
        IChatGui           chatGui,
        IClientState       clientState,
        IFramework         framework,
        IPluginLog         log,
        Configuration      config,
        ISigScanner        sigScanner)
    {
        this.mqttClient     = mqttClient;
        this.commandManager = commandManager;
        this.chatGui        = chatGui;
        this.clientState    = clientState;
        this.framework      = framework;
        this.log            = log;
        this.config         = config;
        this.sigScanner     = sigScanner;

        // Resolve ProcessChatBox function pointer via signature scan
        try
        {
            var fnPtr = sigScanner.ScanText("48 89 5C 24 ?? 57 48 83 EC 20 48 8B FA 48 8B D9 45 84 C9");
            processChatBox = Marshal.GetDelegateForFunctionPointer<ProcessChatBoxDelegate>(fnPtr);
            uiModulePtr    = sigScanner.GetStaticAddressFromSig("48 8B 0D ?? ?? ?? ?? 48 8D 54 24 ?? 48 83 C1 10 E8");
            log.Debug("[CommandReceiver] ProcessChatBox resolved.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[CommandReceiver] Could not resolve ProcessChatBox signature.");
        }

        this.mqttClient.Connected       += OnConnected;
        this.mqttClient.Disconnected    += OnDisconnected;
        this.mqttClient.MessageReceived += OnMessageReceived;
    }

    private string BuildTopic()
    {
        var prefix = config.IncludeClientId && !string.IsNullOrWhiteSpace(config.ClientId)
            ? $"ffxiv/{config.ClientId}"
            : "ffxiv";
        return $"{prefix}/Command/Chat";
    }

    private async void OnConnected(object? sender, EventArgs e)
    {
        if (!config.EnableCommandReceiver) return;
        subscribedTopic = BuildTopic();
        try
        {
            await mqttClient.SubscribeAsync(subscribedTopic, MqttQualityOfServiceLevel.AtLeastOnce);
            log.Information($"[CommandReceiver] Subscribed to '{subscribedTopic}'");
        }
        catch (Exception ex) { log.Error(ex, "[CommandReceiver] Subscribe failed."); }
    }

    private void OnDisconnected(object? sender, EventArgs e) => subscribedTopic = string.Empty;

    private void OnMessageReceived(object? sender, MqttApplicationMessageReceivedEventArgs e)
    {
        if (!string.Equals(e.ApplicationMessage.Topic, subscribedTopic, StringComparison.OrdinalIgnoreCase))
            return;

        var raw = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment).Trim();
        // Strip surrounding quotes HA may add
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1].Trim();
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 500) return;

        log.Debug($"[CommandReceiver] Received: {raw}");
        framework.RunOnFrameworkThread(() => Execute(raw));
    }

    private void Execute(string payload)
    {
#pragma warning disable CS0618
        if (clientState.LocalPlayer == null) return;
#pragma warning restore CS0618

        if (payload.StartsWith('/'))
        {
            // Try Dalamud commands first (plugin commands like /xlsettings etc.)
            if (commandManager.ProcessCommand(payload))
            {
                log.Debug($"[CommandReceiver] Dalamud command: {payload}");
                return;
            }
            // Fall back to native game ChatBox (handles /gearset, /ac, /wait, etc.)
            SendToGameChatBox(payload);
        }
        else
        {
            var msg = new SeStringBuilder()
                .AddUiForeground("[MQTT] ", 32)
                .AddText(payload)
                .Build();
            chatGui.Print(new XivChatEntry { Message = msg });
        }
    }

    private void SendToGameChatBox(string message)
    {
        if (processChatBox == null)
        {
            log.Error("[CommandReceiver] ProcessChatBox not available.");
            chatGui.PrintError($"[MQTT] Cannot send native command: {message}");
            return;
        }

        try
        {
            var bytes  = Encoding.UTF8.GetBytes(message + "\0");
            var handle = Marshal.AllocHGlobal(bytes.Length + 32);
            Marshal.Copy(bytes, 0, handle, bytes.Length);
            processChatBox(uiModulePtr, handle, nint.Zero, 0);
            Marshal.FreeHGlobal(handle);
            log.Debug($"[CommandReceiver] Native command sent: {message}");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[CommandReceiver] SendToGameChatBox failed: {message}");
            chatGui.PrintError($"[MQTT] Failed: {message}");
        }
    }

    public void Dispose()
    {
        mqttClient.Connected       -= OnConnected;
        mqttClient.Disconnected    -= OnDisconnected;
        mqttClient.MessageReceived -= OnMessageReceived;
    }
}