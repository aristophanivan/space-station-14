using Robust.Shared.Serialization;
using Content.Shared.Photography;

namespace Content.Shared.Forensics
{
    [Serializable, NetSerializable]
    public sealed class ForensicPhotoData
    {
        public readonly Vector2i Size;
        public readonly PhotoOrigin Origin;
        public readonly bool IsCopy;

        public ForensicPhotoData(Vector2i size, PhotoOrigin origin, bool isCopy)
        {
            Size = size;
            Origin = origin;
            IsCopy = isCopy;
        }
    }

    [Serializable, NetSerializable]
    public sealed class ForensicScannerBoundUserInterfaceState : BoundUserInterfaceState
    {
        public readonly List<string> Fingerprints = new();
        public readonly List<string> Fibers = new();
        public readonly List<string> TouchDNAs = new();
        public readonly List<string> SolutionDNAs = new();
        public readonly List<string> Residues = new();
        public readonly ForensicPhotoData? PhotoData;
        public readonly string LastScannedName = string.Empty;
        public readonly TimeSpan PrintCooldown = TimeSpan.Zero;
        public readonly TimeSpan PrintReadyAt = TimeSpan.Zero;

        public ForensicScannerBoundUserInterfaceState(
            List<string> fingerprints,
            List<string> fibers,
            List<string> touchDnas,
            List<string> solutionDnas,
            List<string> residues,
            ForensicPhotoData? photoData,
            string lastScannedName,
            TimeSpan printCooldown,
            TimeSpan printReadyAt)
        {
            Fingerprints = fingerprints;
            Fibers = fibers;
            TouchDNAs = touchDnas;
            SolutionDNAs = solutionDnas;
            Residues = residues;
            PhotoData = photoData;
            LastScannedName = lastScannedName;
            PrintCooldown = printCooldown;
            PrintReadyAt = printReadyAt;
        }
    }

    [Serializable, NetSerializable]
    public enum ForensicScannerUiKey : byte
    {
        Key
    }

    [Serializable, NetSerializable]
    public sealed class ForensicScannerPrintMessage : BoundUserInterfaceMessage
    {
    }

    [Serializable, NetSerializable]
    public sealed class ForensicScannerClearMessage : BoundUserInterfaceMessage
    {
    }
}
