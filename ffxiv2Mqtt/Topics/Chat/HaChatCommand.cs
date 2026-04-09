using System;
using Dalamud.Game.Command;
using Ffxiv2Mqtt.Topics;

namespace Ffxiv2Mqtt.Topics.Chat;

/// <summary>
/// Registers the /ha command and publishes its argument as a plaintext
/// MQTT payload to ffxiv/Chat/Message (or ffxiv/{ClientId}/Chat/Message).
///
/// Usage in-game:  /ha LichtAn
/// MQTT payload:   LichtAn
/// </summary>
internal class HaChatCommand : Topic, IDisposable
{
    private const string Command = "/ha";

    protected override string TopicPath => "Chat/Message";
    protected override bool   Retained  => false;

    public HaChatCommand()
    {
        Service.CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Sends a message to Home Assistant via MQTT. Usage: /ha <message>",
            ShowInHelp  = true,
        });
    }

    private void OnCommand(string command, string arguments)
    {
#pragma warning disable CS0618
        if (Service.ClientState.LocalPlayer == null) return;
#pragma warning restore CS0618

        var payload = arguments.Trim();

        if (string.IsNullOrWhiteSpace(payload)) {
            Service.ChatGui.PrintError("[FFXIV2MQTT] Usage: /ha <message>");
            return;
        }

        if (payload.Length > 500) {
            Service.ChatGui.PrintError("[FFXIV2MQTT] Message too long (max 500 characters).");
            return;
        }

        Publish(payload);
        Service.Log.Debug($"[HaChatCommand] Published: {payload}");
    }

    public void Dispose()
    {
        Service.CommandManager.RemoveHandler(Command);
    }
}
