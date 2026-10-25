using System.Globalization;

namespace Strands.Harness.Tools;

internal static class CurlWebFetch
{
    internal const int CaptureBytes = 8 * 1024 * 1024;
    internal static async Task<WebFetchShell> ResolveShellAsync(ISandbox sandbox, WebFetchShell? selected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sandbox is not ICommandSandbox) throw new ArgumentException("The curl web_fetch transport requires an ICommandSandbox. Select Direct explicitly to use host HTTP.", nameof(sandbox));
        if (sandbox is LocalFileSandbox { SupportsHostCommands: false }) throw new PlatformNotSupportedException("The .NET Standard host curl transport supports Windows only. Supply a command-capable sandbox for this platform or explicitly select Direct.");
        if (selected is { } explicitShell && sandbox is not LocalFileSandbox) return explicitShell;
        var platform = sandbox is IEnvironmentSandbox environment ? await environment.GetPlatformAsync(cancellationToken).ConfigureAwait(false) : null;
        WebFetchShell shell;
        if (platform?.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0) shell = WebFetchShell.WindowsPowerShell;
        else if (platform is not null && new[] { "linux", "darwin", "unix", "macos", "freebsd", "openbsd", "netbsd", "sunos" }.Any(name => platform.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)) shell = WebFetchShell.Posix;
        else throw new NotSupportedException("The curl transport requires the selected sandbox's command language. Set WebFetchConfig.Shell explicitly or provide a recognized IEnvironmentSandbox platform; the host platform is never substituted.");
        if (selected is { } requested && requested != shell) throw new ArgumentException("The requested web_fetch command language is incompatible with LocalFileSandbox's selected shell.", nameof(selected));
        return shell;
    }

    internal static async Task<FetchedPage> FetchAsync(ISandbox sandbox, string url, WebFetchConfig config, CancellationToken cancellationToken)
    {
        var shell = await ResolveShellAsync(sandbox, config.Shell, cancellationToken).ConfigureAwait(false);
        var command = shell == WebFetchShell.WindowsPowerShell ? PowerShell(url, config.Timeout) : Posix(url, config.Timeout);
        var result = await ((ICommandSandbox)sandbox).ExecuteAsync(command, new() { Timeout = config.Timeout + TimeSpan.FromSeconds(5), MaxOutputBytes = CaptureBytes }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.OutputTruncated) throw new IOException("web_fetch capture was truncated by the sandbox's output policy; the partial body was discarded.");
        if (result.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(result.StandardError) ? "curl exited with code " + result.ExitCode.ToString(CultureInfo.InvariantCulture) : result.StandardError.Trim());
        var first = result.StandardOutput.IndexOf('\n');
        var second = first < 0 ? -1 : result.StandardOutput.IndexOf('\n', first + 1);
        if (first < 0 || second < 0) throw new InvalidDataException("The curl response did not contain its content-type and resolved-URL headers.");
        var contentType = result.StandardOutput.Substring(0, first).Trim();
        var resolved = result.StandardOutput.Substring(first + 1, second - first - 1).Trim();
        if (resolved.Length > 0) WebFetchContent.ValidateUrl(resolved);
        var encoded = new string(result.StandardOutput.Substring(second + 1).Where(character => !char.IsWhiteSpace(character)).ToArray());
        if (encoded.Length > ((WebFetch.MaxBytes + 2) / 3) * 4) throw new InvalidDataException("The curl body exceeded its five-MiB byte limit.");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > WebFetch.MaxBytes) throw new InvalidDataException("The curl body exceeded its five-MiB byte limit.");
        return WebFetchContent.Convert(bytes, contentType, resolved, url);
    }

    private static string Posix(string url, TimeSpan timeout)
    {
        // Only the capped body reaches disk. A closed head pipe may produce curl exit 23; accept it
        // only when the exact byte limit was reached and curl still supplied complete response metadata.
        // Parse final response headers: curl's content_type metadata can retain a redirect's value.
        const string script = """
            d=$(mktemp -d -t strands-web-fetch.XXXXXX) || exit 1
            h=
            cleanup() { if [ -n "$h" ]; then kill "$h" 2>/dev/null; wait "$h" 2>/dev/null; fi; rm -f "$d/body" "$d/meta" "$d/status" "$d/headers" "$d/header-pipe"; rmdir "$d"; }
            trap cleanup 0
            mkfifo "$d/header-pipe" || exit 1
            head -c 2097153 < "$d/header-pipe" > "$d/headers" & h=$!
            (curl -sSL -g --fail --proto '=http,https' --proto-redir '=http,https' --max-redirs 20 --max-time __SECONDS__ -A 'strands-harness/1.0' --dump-header "$d/header-pipe" -w '%{stderr}\nSTRANDS_WEB_FETCH_METADATA\n%{url_effective}\n' -- __URL__; printf '%s\n' "$?" > "$d/status") 2> "$d/meta" | head -c __BYTES__ > "$d/body"
            i=0
            while [ ! -s "$d/status" ]; do i=$((i+1)); if [ "$i" -gt 5 ]; then printf '%s\n' 'curl did not publish its exit status' >&2; exit 1; fi; sleep 1; done
            s=$(cat "$d/status")
            n=$(wc -c < "$d/body")
            if [ "$s" -ne 0 ] && ! { [ "$s" -eq 23 ] && [ "$n" -eq __BYTES__ ]; }; then cat "$d/meta" >&2; exit "$s"; fi
            wait "$h"
            header_status=$?
            h=
            if [ "$header_status" -ne 0 ]; then exit 1; fi
            if [ "$(wc -c < "$d/headers")" -gt 2097152 ]; then printf '%s\n' 'curl response headers exceeded two MiB' >&2; exit 1; fi
            content_type=$(LC_ALL=C awk '
              /^HTTP\/[^ ]+ [0-9][0-9][0-9]/ { blocks++; bytes=0; type="" }
              { bytes+=length($0)+1; if(bytes>65536) invalid=1; line=$0; sub(/\r$/, "", line) }
              tolower(line) ~ /^content-type:/ { sub(/^[^:]*:[ \t]*/, "", line); type=line }
              END { if(invalid || blocks<1 || blocks>32) exit 1; print type }
            ' "$d/headers") || { printf '%s\n' 'curl returned invalid or oversized response headers' >&2; exit 1; }
            if ! grep -q '^STRANDS_WEB_FETCH_METADATA$' "$d/meta"; then printf '%s\n' 'curl did not return bounded response metadata' >&2; exit 1; fi
            printf '%s\n' "$content_type"
            sed -n '/^STRANDS_WEB_FETCH_METADATA$/,$p' "$d/meta" | tail -n +2
            base64 < "$d/body"
            """;
        return script.Replace("__SECONDS__", timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture))
            .Replace("__BYTES__", WebFetch.MaxBytes.ToString(CultureInfo.InvariantCulture))
            .Replace("__URL__", "'" + url.Replace("'", "'\\''") + "'");
    }

    private static string PowerShell(string url, TimeSpan timeout)
    {
        // Reading curl's include stream avoids temporary files and bounds the downloaded body before
        // base64 encoding. PowerShell's native-process wrapper does not decode the binary body.
        const string script = """
            $url=__URL__
            $resolved=$url
            $max=__BYTES__
            $native=Get-Command -Name curl.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if($null -ne $native) { $curlPath=$native.Source }
            else {
              # A 32-bit shell sees redirected System32. Sysnative addresses this same environment's system curl.
              $curlPath=Join-Path $env:SystemRoot 'Sysnative\curl.exe'
              if(-not (Test-Path -LiteralPath $curlPath -PathType Leaf)) { throw 'curl.exe was not found on the selected sandbox PATH or in its Sysnative system directory' }
            }
            $psi=New-Object Diagnostics.ProcessStartInfo
            $psi.FileName=$curlPath
            $psi.Arguments='-sSL -g --fail --include --suppress-connect-headers --proto "=http,https" --proto-redir "=http,https" --max-redirs 20 --max-time __SECONDS__ -A "strands-harness/1.0" -- "'+$url+'"'
            $psi.UseShellExecute=$false
            $psi.CreateNoWindow=$true
            $psi.RedirectStandardOutput=$true
            $psi.RedirectStandardError=$true
            $p=New-Object Diagnostics.Process
            $p.StartInfo=$psi
            $started=$false
            $body=New-Object IO.MemoryStream
            function Read-ResponseHeader($stream) {
              $bytes=New-Object 'Collections.Generic.List[byte]'
              while($bytes.Count -lt 65536) {
                $next=$stream.ReadByte()
                if($next -lt 0) { throw 'curl ended before completing response headers' }
                $bytes.Add([byte]$next)
                $n=$bytes.Count
                if($n -ge 4 -and $bytes[$n-4] -eq 13 -and $bytes[$n-3] -eq 10 -and $bytes[$n-2] -eq 13 -and $bytes[$n-1] -eq 10) {
                  return [Text.Encoding]::ASCII.GetString($bytes.ToArray())
                }
              }
              throw 'curl response headers exceeded 64 KiB'
            }
            try {
              if(-not $p.Start()) { throw 'curl process did not start' }
              $started=$true
              $errors=$p.StandardError.ReadToEndAsync()
              $stream=$p.StandardOutput.BaseStream
              $contentType=''
              for($block=0; $block -lt 32; $block++) {
                $contentType=''
                $header=Read-ResponseHeader $stream
                $lines=$header.Split([string[]]@("`r`n"), [StringSplitOptions]::None)
                $match=[Text.RegularExpressions.Regex]::Match($lines[0], '^HTTP/\S+\s+(\d{3})')
                if(-not $match.Success) { throw 'curl returned invalid HTTP response headers' }
                $status=[int]$match.Groups[1].Value
                $location=''
                foreach($line in $lines) {
                  if($line.StartsWith('Content-Type:', [StringComparison]::OrdinalIgnoreCase)) { $contentType=$line.Substring(13).Trim() }
                  if($line.StartsWith('Location:', [StringComparison]::OrdinalIgnoreCase)) { $location=$line.Substring(9).Trim() }
                }
                if($status -ge 100 -and $status -lt 200) { continue }
                if($status -ge 300 -and $status -lt 400 -and $location.Length -gt 0) { $resolved=([Uri]::new([Uri]$resolved,$location)).AbsoluteUri; continue }
                break
              }
              if($block -ge 32) { throw 'curl exceeded the bounded response-header count' }
              $buffer=New-Object byte[] 16384
              while($body.Length -lt $max) {
                $read=$stream.Read($buffer,0,[int][Math]::Min($buffer.Length,$max-$body.Length))
                if($read -eq 0) { break }
                $body.Write($buffer,0,$read)
              }
              $capped=$body.Length -eq $max
              if($capped -and -not $p.HasExited) { $p.Kill() }
              $p.WaitForExit()
              $errorText=$errors.GetAwaiter().GetResult()
              if($p.ExitCode -ne 0 -and -not $capped) { throw ('curl exited with code '+$p.ExitCode+': '+$errorText.Trim()) }
              [Console]::Write($contentType+"`n"+$resolved+"`n"+[Convert]::ToBase64String($body.ToArray()))
            } finally {
              if($started) { if(-not $p.HasExited) { $p.Kill() }; $null=$p.WaitForExit(2000) }
              $body.Dispose()
              $p.Dispose()
            }
            """;
        return script.Replace("__SECONDS__", timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture))
            .Replace("__BYTES__", WebFetch.MaxBytes.ToString(CultureInfo.InvariantCulture))
            .Replace("__URL__", "'" + url.Replace("'", "''") + "'");
    }
}
