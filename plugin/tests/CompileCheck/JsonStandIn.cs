// Compile-check only: System.Text.Json is part of .NET 9/10 but not of the .NET Framework profile used here.
namespace System.Text.Json
{
    public sealed class JsonSerializerOptions { public bool WriteIndented { get; set; } }
    public static class JsonSerializer
    {
        public static T Deserialize<T>(string json) => default(T);
        public static string Serialize<T>(T value, JsonSerializerOptions options = null) => "";
    }
}

namespace System.Text.Json.Serialization
{
    [System.AttributeUsage(System.AttributeTargets.Property)]
    public sealed class JsonIgnoreAttribute : System.Attribute { }
}
