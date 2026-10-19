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
        if (kind != "air" && kind != "helicopter" && kind != "submarine") return;
        // The contact UI displays Object.Altitude in feet; submarine depth negates that same value.
        // Do not read vertical status for surface/unknown kinds or infer a type from the actual target.
        var altitude = Member(Required(vehicle, "Object"), "Altitude");
        var value = altitude == null ? null : Member(altitude, "Value");
        var key = kind == "submarine" ? "depth_ft" : "altitude_ft";
        contact[key] = value == null ? JValue.CreateNull() :
            new JValue((float)Number(value) * (kind == "submarine" ? -1f : 1f));
    }
}
