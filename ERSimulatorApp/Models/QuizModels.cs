using System.Collections.Generic;

namespace ERSimulatorApp.Models;

public class QuizQuestion
{
    public string Question { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
    public int CorrectIndex { get; set; }
    public string Rationale { get; set; } = string.Empty;
    public List<string> SourceHints { get; set; } = new();
}

public class QuizResponse
{
    public List<QuizQuestion> Questions { get; set; } = new();
    public List<ChatSourceLink> Sources { get; set; } = new();
    public string? Message { get; set; }
}










