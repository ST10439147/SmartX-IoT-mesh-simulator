using System;
using System.Collections.Generic;
using System.Text;

namespace SmartX.Shared.Classes.Telemetry
{
    /// <summary>
    /// Represents the category of a sensor, which can be used to classify sensors based on their functionality or purpose.
    /// What the node is measuring. Drives grouping and default thresholds.
    /// </summary>
    public enum SensorCategory
    {
        Environmental,
        PowerConsumption,
        Actuator
    }

    /// <summary>
    /// Which CLR (Common Language Runtime) value type the node publishes. This is what lets the gateway pick the 
    /// correct type to use when deserializing the payload. 
    /// This is also used to determine the correct type to use when storing the value in the database.
    /// </summary>
    public enum payloadType
    {
        Integer,
        Float,
        Boolean
    }
    internal class SensorReg
    {
    }
}
