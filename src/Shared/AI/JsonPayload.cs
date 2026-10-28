using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

internal static class JsonPayload
{
    internal static int Bytes(JToken value) => Encoding.UTF8.GetByteCount(value.ToString(Formatting.None));
}
