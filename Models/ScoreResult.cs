namespace KRBWeekly.Models;

/// <summary>
/// 计分结果
/// </summary>
public class ScoreResult
{
    public double Total { get; set; }
    public double CoefA { get; set; }
    public double CoefB { get; set; }
    public double CoefC { get; set; }
    public double PlayScore { get; set; }
    public double FavScore { get; set; }
    public double LikeCoinScore { get; set; }
}
