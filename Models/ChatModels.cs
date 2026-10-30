using System.ComponentModel.DataAnnotations;

namespace LedgerMind.Models;

public class ChatTurn
{
    // Sirf "user" aur "assistant" maane jate hain, baqi server par chhod diye jate hain.
    // Nullable: null role/content wala turn 400 nahi deta, chupchap chhod diya jata hai.
    public string? Role { get; set; }
    public string? Content { get; set; }
}

public class ChatRequest
{
    public const int MaxMessageLength = 2000;

    // UI aakhri 10 bhejti hai; is se zyada bilkul bekaar payload hai
    public const int MaxHistoryItems = 50;

    [Required]
    [MaxLength(MaxMessageLength)]
    public string Message { get; set; } = string.Empty;

    [MaxLength(MaxHistoryItems)]
    public List<ChatTurn>? History { get; set; }
}
