using System;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Ffxiv2Mqtt.Services;

public sealed unsafe class MqttCommandReceiver : IDisposable
{
    private readonly IMqttClientWrapper mqttClient;
    private readonly ICommandManager    commandManager;
    private readonly IChatGui           chatGui;
    private readonly IClientState       clientState;
    private readonly IFramework         framework;
    private readonly IPluginLog         log;
    private readonly Configuration      config;

    private string subscribedTopic = string.Empty;

    public MqttCommandReceiver(
        IMqttClientWrapper mqttClient,
        ICommandManager    commandManager,
        IChatGui           chatGui,
        IClientState       clientState,
        IFramework         framework,
        IPluginLog         log,
        Configuration      config)
    {
        this.mqttClient     = mqttClient;
        this.commandManager = commandManager;
        this.chatGui        = chatGui;
        this.clientState    = clientState;
        this.framework      = framework;
        this.log            = log;
        this.config         = config;

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
            if (commandManager.ProcessCommand(payload))
            {
                log.Debug($"[CommandReceiver] Dalamud command: {payload}");
                return;
            }
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
        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule == null) { log.Error("[CommandReceiver] UIModule null."); return; }

            var utf8 = new Utf8String();
            utf8.SetString(message);
            uiModule->ProcessChatBoxEntry(&utf8);
            utf8.Dtor();

            log.Debug($"[CommandReceiver] Native command: {message}");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[CommandReceiver] Failed: {message}");
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