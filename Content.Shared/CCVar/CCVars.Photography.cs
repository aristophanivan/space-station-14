using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    public static readonly CVarDef<bool> PhotographyUploadsEnabled =
        CVarDef.Create("photography.uploads_enabled", true, CVar.SERVERONLY);

    public static readonly CVarDef<int> PhotographyMaxUploadsPerRound =
        CVarDef.Create("photography.max_uploads_per_round", 128, CVar.SERVERONLY);

    public static readonly CVarDef<int> PhotographyMaxUploadsPerUser =
        CVarDef.Create("photography.max_uploads_per_user", 16, CVar.SERVERONLY);

    public static readonly CVarDef<int> PhotographyMaxDimension =
        CVarDef.Create("photography.max_dimension", 1024, CVar.SERVERONLY);

    public static readonly CVarDef<int> PhotographyMaxDecodedBytes =
        CVarDef.Create("photography.max_decoded_bytes", 4 * 1024 * 1024, CVar.SERVERONLY);

    public static readonly CVarDef<int> PhotographyMaxEncodedBytes =
        CVarDef.Create("photography.max_encoded_bytes", 512 * 1024, CVar.SERVERONLY);

    public static readonly CVarDef<int> PhotographyRoundStorageBytes =
        CVarDef.Create("photography.round_storage_bytes", 1024 * 1024 * 1024, CVar.SERVERONLY);

    public static readonly CVarDef<int> PhotographyMaxImageRecords =
        CVarDef.Create("photography.max_image_records", 65536, CVar.SERVERONLY);

    public static readonly CVarDef<int> PhotographyProcessingWorkers =
        CVarDef.Create("photography.processing_workers", 2, CVar.SERVERONLY);

    public static readonly CVarDef<int> PhotographyProcessingQueueCapacity =
        CVarDef.Create("photography.processing_queue_capacity", 8, CVar.SERVERONLY);
}
