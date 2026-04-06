using System;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Ffxiv2Mqtt.Services;

/// <summary>
/// Minimal interface that MqttManager must implement so that
/// MqttCommandReceiver can hook into the client lifecycle without
/// depending on the concrete MqttManager class directly.
/// </summary>
public interface IMqttClientWrapper
{
    /// <summary>Raised when the MQTT connection is established.</summary>
    event EventHandler? Connected;

    /// <summary>Raised when the MQTT connection is lost or closed.</summary>
    event EventHandler? Disconnected;

    /// <summary>Raised for every incoming MQTT message.</summary>
    event EventHandler<MqttApplicationMessageReceivedEventArgs>? MessageReceived;

    /// <summary>Subscribe to a topic.</summary>
    Task SubscribeAsync(string topic, MqttQualityOfServiceLevel qos);
}