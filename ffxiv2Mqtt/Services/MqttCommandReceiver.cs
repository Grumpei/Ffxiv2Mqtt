using System;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Ffxiv2Mqtt.Services;

/// <summary>
/// Subscribes to MQTT and forwards payloads as in-game commands or chat messages.
///
/// Topic:   ffxiv/Command/Chat  (or ffxiv/{ClientId}/Command/Chat)
///
/// Payload rules:
///   Starts with '/'  -> executed as slash command
///   Plain text       -> printed to local chat with [MQTT] prefix
/// </summary>
public sealed class MqttCommandReceiver : IDisposable
{
    private const string RelayCommand = "/mqttrelay";

    private readonly IMqttClientWrapper mqttClient;
    private readonly ICommandManager    commandManager;
    private readonly IChatGui           chatGui;
    private readonly IClientState       clientState;
    private readonly IFramework         framework;
    private readonly IPluginLog         log;
    private readonly Configuration      config;

    private string subscribedTopic = string.Empty;

    // Queue for the relay command to process
    private string? pendingCommand;

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

        // Register a relay command that the game processes natively
        commandManager.AddHandler(RelayCommand, new CommandInfo(OnRelayCommand)
        {
            ShowInHelp = false,
        });
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

        // Strip surrounding quotes that Home Assistant may add
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
            // Try Dalamud-registered commands first (plugin commands)
            if (commandManager.ProcessCommand(payload))
            {
                log.Debug($"[CommandReceiver] Dalamud command executed: {payload}");
                return;
            }

            // For native game commands: use the relay trick
            // Store the actual command and trigger our relay handler
            // which Dalamud will process — passing the args to the game shell
            pendingCommand = payload;
            commandManager.ProcessCommand(RelayCommand + " " + payload.TrimStart('/'));
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

    private void OnRelayCommand(string command, string args)
    {
        // The pending command is the full slash command from MQTT
        var cmd = pendingCommand;
        pendingCommand = null;

        if (string.IsNullOrEmpty(cmd)) return;

        // Use XivCommon-style: dispatch via the game's own command handler
        // by processing it as the full slash command the game understands
        chatGui.Print(new XivChatEntry
        {
            Type    = XivChatType.Debug,
            Message = new SeStringBuilder().AddText(cmd).Build(),
        });

        // Actually execute via ProcessCommand with the real command
        // The game processes /gearset, /ac etc via its own pipeline
        Service.Framework.RunOnTick(() =>
        {
            if (!commandManager.ProcessCommand(cmd))
                log.Warning($"[CommandReceiver] Command not processed by Dalamud: {cmd}");
        });
    }

    public void Dispose()
    {
        commandManager.RemoveHandler(RelayCommand);
        mqttClient.Connected       -= OnConnected;
        mqttClient.Disconnected    -= OnDisconnected;
        mqttClient.MessageReceived -= OnMessageReceived;
    }
}