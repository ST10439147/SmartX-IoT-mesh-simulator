using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>
/// One serialiser configuration shared by the API, the SignalR hub and the client.
/// Keeping it in the shared library is what stops the two ends drifting - a converter
/// added on the server and forgotten on the client is a silent deserialisation failure.
/// </summary>
public static class SmartXJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Applies the SmartX conventions to an existing options object.</summary>
    // Reference [2]: Microsoft. How to serialize and deserialize JSON using System.Text.Json. Microsoft Learn.
    //   https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/how-to
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        // Enums travel as names, so a payload reads "SoilMoisture" rather than "0" -
        // worth it for anyone poking the API with curl.
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>Names of the messages the telemetry hub pushes. Shared so client and server cannot drift.</summary>
public static class HubEvents
{
    public const string Tick = "tick";
    public const string AlertRaised = "alertRaised";
    public const string AlertDismissed = "alertDismissed";
    public const string SensorsChanged = "sensorsChanged";
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
