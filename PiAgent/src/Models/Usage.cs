using System.Text.Json.Serialization;

namespace PiAgent.Models
{
    /// <summary>
    /// Token usage statistics from an LLM response.
    /// </summary>
    public class Usage
    {
        [JsonPropertyName("input")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output")]
        public int OutputTokens { get; set; }

        [JsonPropertyName("total")]
        public int TotalTokens { get; set; }

        public Usage() { }

        public Usage(int input, int output, int total)
        {
            InputTokens = input;
            OutputTokens = output;
            TotalTokens = total;
        }

        public static Usage Zero { get; } = new Usage(0, 0, 0);
    }
}
