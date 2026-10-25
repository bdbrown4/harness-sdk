using System.Text.Json;
using System.Text.RegularExpressions;
using Strands.Agents;
using Strands.Agents.Files;

namespace Strands.Harness;

internal sealed class ReadFileTool(IReadOnlyFileSystem? fileSystem, ITool text, ReadConfig options, IModel? model, string? identifier, bool rebindOnDelegation) : IRichTool
{
    private readonly bool _rebindOnDelegation = rebindOnDelegation;
    private readonly ReadPagingProfile _pagingProfile = options.PagingProfile;
    private static readonly IReadOnlyDictionary<string, string> Images = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["webp"] = "image/webp"
    };
    private static readonly IReadOnlyDictionary<string, string> Documents = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["pdf"] = "application/pdf",
        ["doc"] = "application/msword",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xls"] = "application/vnd.ms-excel",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    };
    public ToolSpec Spec { get; } = new(text.Spec.Name, "Read a file. Text returns numbered lines; images (png/jpg/jpeg/gif/webp) and binary documents (pdf/doc/docx/xls/xlsx) return viewable media. Text defaults to the first 2000 lines; use offset/limit to page through larger files.", text.Spec.InputSchema);

    internal static ITool ForModel(ITool tool, IModel childModel, string? childIdentifier) => tool is ReadFileTool read && read._rebindOnDelegation ? read.Rebind(childModel, childIdentifier) : tool;
    private ReadFileTool Rebind(IModel childModel, string? childIdentifier) => new(fileSystem, text, options, childModel, childIdentifier, _rebindOnDelegation);
    public async ValueTask<string> InvokeAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var result = await InvokeResultAsync(input, context, cancellationToken).ConfigureAwait(false);
        return result.Content;
    }

    public async ValueTask<ToolResultBlock> InvokeResultAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pagingProfile != ReadPagingProfile.NativeIntegral) _ = ReadPaging.Parse(input, _pagingProfile);
        var path = input.GetProperty("path").GetString() ?? throw new ArgumentException("path must be a string.", nameof(input));
        FileTools.ValidatePath(path);
        var name = path.Split(new[] { '/', '\\' }).Last();
        var dot = name.LastIndexOf('.');
        var extension = dot < 0 ? string.Empty : name.Substring(dot + 1).ToLowerInvariant();
        var image = Images.TryGetValue(extension, out var imageType);
        var document = Documents.TryGetValue(extension, out var documentType);
        if (!image && !document)
            return new(context.ToolUse.ToolUseId, await text.InvokeAsync(input, context, cancellationToken).ConfigureAwait(false));
        var bytes = options.MaximumMediaBytes is { } maximum
            ? fileSystem is IBoundedFileReader bounded ? await bounded.ReadBytesAsync(path, maximum, cancellationToken).ConfigureAwait(false)
                : throw new NotSupportedException("An explicit media byte limit requires the selected filesystem's IBoundedFileReader capability.")
            : fileSystem is IFileSystem files ? await files.ReadBytesAsync(path, cancellationToken).ConfigureAwait(false)
                : throw new NotSupportedException("The selected sandbox does not provide binary file reads required for image and document files.");
        cancellationToken.ThrowIfCancellationRequested();
        if (options.MaximumMediaBytes is { } limit && bytes.Length > limit) throw new IOException("The media file exceeds the configured byte limit.");
        if (!(options.Media ?? Models.ReadMediaPolicy.Supports(model, identifier)))
        {
            var kind = image ? "Image" : "Document";
            var format = extension == "jpg" ? "jpeg" : extension;
            return new(context.ToolUse.ToolUseId, $"[{kind} file: {path} ({format}, {bytes.Length} bytes). This model cannot view {kind.ToLowerInvariant()} files, so its contents are not shown. Use shell tools if the file has a text-extractable form.]");
        }
        var encoded = Convert.ToBase64String(bytes);
        ContentBlock block = image ? new ImageBlock(imageType!, encoded) : new DocumentBlock(documentType!, encoded, DocumentName(name));
        return new(context.ToolUse.ToolUseId, new[] { block });
    }

    private static string DocumentName(string name)
    {
        var sanitized = Regex.Replace(Regex.Replace(name, @"[^a-zA-Z0-9\s\-()\[\]]", " "), @"\s+", " ").Trim();
        return sanitized.Length == 0 ? "document" : sanitized;
    }
}
