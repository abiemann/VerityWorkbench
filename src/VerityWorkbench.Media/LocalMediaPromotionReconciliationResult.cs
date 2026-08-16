namespace VerityWorkbench.Media;

public sealed record LocalMediaPromotionReconciliationResult(
    int CompletedCommittedPromotions,
    int RolledBackUncommittedPromotions,
    int ClearedPreparedPromotions,
    int WarningCount);
