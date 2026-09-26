namespace KRBWeekly.Services;
using KRBWeekly.Models;

public static class ScoreCalculator
{
    public static ScoreResult CalcScore(
        long deltaView, long deltaFavorite, long deltaLike, long deltaCoin,
        double daysDiff, ScoringConfig? config = null)
    {
        config ??= new ScoringConfig();

        // 负增量按 0 计（B 站风控回调播放量不倒扣分）；四项全无增量仍为 0 分
        double view = Math.Max(deltaView, 0);
        double fav = Math.Max(deltaFavorite, 0);
        double like = Math.Max(deltaLike, 0);
        double coin = Math.Max(deltaCoin, 0);
        if (view == 0 && fav == 0 && like == 0 && coin == 0)
            return new ScoreResult();

        double A = Math.Max(config.AMax - daysDiff / config.ADecay, config.AMin);
        // 无播放增量时赞/藏/币比例系数无意义，B、C 归 1.0 按原始增量求和（防除零爆表）
        double B = view > 0 ? Math.Min((fav / view) * config.BMultiplier, config.BMax) : 1.0;
        double C = view > 0 ? Math.Min((Math.Min(like, coin) / view) * config.CMultiplier, config.CMax) : 1.0;

        double playScore = view * A;
        double favScore = fav * B;
        double likeCoinScore = (like + coin) * C;

        return new ScoreResult
        {
            Total = playScore + favScore + likeCoinScore,
            CoefA = A, CoefB = B, CoefC = C,
            PlayScore = playScore, FavScore = favScore, LikeCoinScore = likeCoinScore
        };
    }
}
