namespace Blockbuster.Core.Media;

public sealed record LibrarySource(
    string Id,
    MediaKind Kind,
    string RootPath);
