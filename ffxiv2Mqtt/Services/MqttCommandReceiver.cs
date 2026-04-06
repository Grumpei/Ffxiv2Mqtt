using System;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using MQTTnet;
using MQTTnet.Protocol;

namespace Ffxiv2Mqtt.Services;

/// <summary>
/// Subscribes to the MQTT command topic and forwards received payloads
/// into the FFXIV in-game chat / command pipeline.
///
/// Topic layout:
///   Default:   ffxiv/Command/Chat
///   With ID:   ffxiv/{ClientId}/Command/Chat
///
/// Payload rules:
///   Starts with '/'  -> slash command via ICommandManager or native ChatBox
///   Plain text       -> printed to local chat as info message
///
/// Safety guards:
///   - Max payload length: 500 chars
///   - Always marshalled onto Framework thread
///   - Only runs while a local player is logged in
/// </summary>
public sealed class MqttCommandReceiver : IDisposable
{
    private readonly IMqttClientWrapper    mqttClient;
    private readonly ICommandManager       commandManager;
    private readonly IChatGui              chatGui;
    private readonly IClientState          clientState;
    private readonly IFramework            framework;
    private readonly IPluginLog            log;
    private readonly Configuration         config;

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
        if (!config.EnableCommandReceiver)
        {
            log.Debug("[CommandReceiver] Disabled in config - skipping subscribe.");
            return;
        }

        subscribedTopic = BuildTopic();
        try
        {
            await mqttClient.SubscribeAsync(subscribedTopic, MqttQualityOfServiceLevel.AtLeastOnce);
            log.Information($"[CommandReceiver] Subscribed to '{subscribedTopic}'");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[CommandReceiver] Failed to subscribe.");
        }
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        subscribedTopic = string.Empty;
    }

    private void OnMessageReceived(object? sender, MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        if (!string.Equals(topic, subscribedTopic, StringComparison.OrdinalIgnoreCase))
            return;

        var raw = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

        if (string.IsNullOrWhiteSpace(raw))
        {
            log.Warning("[CommandReceiver] Received empty payload - ignored.");
            return;
        }

        if (raw.Length > 500)
        {
            log.Warning($"[CommandReceiver] Payload too long ({raw.Length} chars) - ignored.");
            return;
        }

        log.Debug($"[CommandReceiver] Incoming on '{topic}': {raw}");
        framework.RunOnFrameworkThread(() => Execute(raw.Trim()));
    }

    private void Execute(string payload)
    {
        if (clientState.LocalPlayer == null)
        {
            log.Warning("[CommandReceiver] Command received but no player is logged in - ignored.");
            return;
        }

        if (payload.StartsWith('/'))
            ExecuteCommand(payload);
        else
        {
            var msg = new SeStringBuilder()
                .AddUiForeground("[MQTT] ", 32)
                .AddText(payload)
                .Build();
            chatGui.Print(new XivChatEntry { Message = msg });
            log.Debug($"[CommandReceiver] Printed plain-text: {payload}");
        }
    }

    private void ExecuteCommand(string command)
    {
        // 1. Try Dalamud-registered commands first
        if (commandManager.ProcessCommand(command))
        {
            log.Debug($"[CommandReceiver] Dispatched Dalamud command: {command}");
            return;
        }

        // 2. Fall back to native game input (/gearset, /micon, etc.)
        try
        {
            ChatBoxHelper.SendMessage(command);
            log.Debug($"[CommandReceiver] Sent native game command: {command}");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[CommandReceiver] Failed to send native command: {command}");
            chatGui.PrintError($"[MQTT] Command failed: {command}");
        }
    }

    public void Dispose()
    {
        mqttClient.Connected       -= OnConnected;
        mqttClient.Disconnected    -= OnDisconnected;
        mqttClient.MessageReceived -= OnMessageReceived;
    }
}