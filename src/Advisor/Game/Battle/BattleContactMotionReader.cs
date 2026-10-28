using System;
using Newtonsoft.Json.Linq;
using static SeaPowerAIAdvisor.BattleValue;

namespace SeaPowerAIAdvisor;

// Copies the values used by ContactBaseViewModel, without constructing UI or reading target capabilities.
internal static class BattleContactMotionReader
{
    internal static void CopyTo(JObject contact, object vehicle)
    {
        var velocity = Member(vehicle, "UnityVelocityVector");
        contact["speed_knots"] = JValue.CreateNull();
        contact["heading_deg"] = JValue.CreateNull();
        if (velocity != null)
        {
            var x = (float)Number(Required(velocity, "x"));
            var y = (float)Number(Required(velocity, "y"));
            var z = (float)Number(Required(velocity, "z"));
            // These are the game's contact-status conversions from Unity velocity to knots/degrees.
            var speed = (float)Math.Sqrt(x * x + y * y + z * z) * 67.20007f * 1.94384f;
            var heading = (float)Math.Atan2(x, z) * 57.29578f;
            if (heading < 0) heading += 360;
            _ = Number(speed);
            _ = Number(heading);
            contact["speed_knots"] = new JValue(speed);
            contact["heading_deg"] = new JValue(heading);
        }
        var kind = (string)contact["kind"]!;
        var key = BattleVerticalPosition.FieldName(kind);
        if (key == null) return;
        // Read only the displayed kind's vertical status, without inferring the actual target type.
        var altitude = Member(Required(vehicle, "Object"), "Altitude");
        var value = altitude == null ? null : Member(altitude, "Value");
        contact[key] = BattleVerticalPosition.Read(kind, value);
    }
}
