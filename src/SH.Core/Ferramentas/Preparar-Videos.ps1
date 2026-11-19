param(
    [Parameter(Mandatory=$true)][string]$Iso,
    [Parameter(Mandatory=$true)][string]$Ffmpeg,
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Videos'),
    [ValidateRange(0,18)][int[]]$MovieNumbers = @(18) + @(0..17),
    [switch]$ReplaceExisting
)
$ErrorActionPreference = 'Stop'
$Iso = (Resolve-Path -LiteralPath $Iso).Path
$Ffmpeg = (Resolve-Path -LiteralPath $Ffmpeg).Path
$Destination = [IO.Path]::GetFullPath($Destination)

if (!('RingPss' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;

public static class RingPss {
    public static void Extract(Stream input, long offset, long size, string path) {
        if (offset < 0 || size < 4 || offset > input.Length - size)
            throw new InvalidDataException("PSS fora dos limites da ISO.");
        input.Position = offset;
        byte[] block = new byte[1024 * 1024];
        using (Stream output = File.Create(path)) {
            while (size > 0) {
                int count = input.Read(block, 0, (int)Math.Min(size, block.Length));
                if (count == 0) throw new EndOfStreamException();
                output.Write(block, 0, count);
                size -= count;
            }
        }
    }

    // Nesta edicao, o stream privado 0x90 carrega PCM stereo LE de 48 kHz.
    // O programa original pula quatro bytes privados antes de enviar o PCM.

    // Todos os PSS desta ISO usam blocos de 256 amostras do canal esquerdo
    // seguidos de 256 do direito. O bloco pode atravessar pacotes PES.
    public static void InterleavePssStereo(string pcm) {
        byte[] source = File.ReadAllBytes(pcm);
        const int channelBytes = 512;
        if (source.Length % (2 * channelBytes) != 0)
            throw new InvalidDataException("Bloco PCM estereo incompleto.");
        byte[] stereo = new byte[source.Length];
        for (int pos = 0; pos < source.Length; pos += 2 * channelBytes) {
            for (int i = 0; i < channelBytes; i += 2) {
                int dest = pos + 2 * i;
                stereo[dest] = source[pos+i];
                stereo[dest+1] = source[pos+i+1];
                stereo[dest+2] = source[pos+channelBytes+i];
                stereo[dest+3] = source[pos+channelBytes+i+1];
            }
        }
        File.WriteAllBytes(pcm, stereo);
    }
    private static void CopyBytes(Stream input, Stream output, int bytes) {
        byte[] buffer = new byte[8192];
        while (bytes > 0) {
            int count = input.Read(buffer, 0, Math.Min(bytes, buffer.Length));
            if (count == 0) throw new EndOfStreamException();
            output.Write(buffer, 0, count);
            bytes -= count;
        }
    }

    public static long ExtractPcm(string pss, string pcm) {
        long bytes = 0;
        using (FileStream input = File.OpenRead(pss))
        using (BinaryReader reader = new BinaryReader(input))
        using (Stream output = File.Create(pcm)) {
            while (input.Position + 4 <= input.Length) {
                long packet = input.Position;
                if ((reader.ReadUInt32() & 0x00ffffff) != 0x00010000)
                    throw new InvalidDataException("Prefixo MPEG invalido no offset " + packet);
                input.Position = packet + 3;
                int type = reader.ReadByte();
                if (type == 0xb9) break;
                if (type == 0xba) {
                    if (input.Length - packet < 14) throw new EndOfStreamException();
                    input.Position = packet + 13;
                    int stuffing = reader.ReadByte() & 7;
                    input.Position += stuffing;
                    continue;
                }

                int length = (reader.ReadByte() << 8) | reader.ReadByte();
                long end = input.Position + length;
                if (end > input.Length) throw new EndOfStreamException();
                if (type == 0xbd) {
                    if (length < 7) throw new InvalidDataException("PES privado incompleto.");
                    int flags = reader.ReadByte();
                    reader.ReadByte();
                    int header = reader.ReadByte();
                    if ((flags & 0xc0) != 0x80 || header > length - 7)
                        throw new InvalidDataException("Cabecalho PES de audio invalido.");
                    input.Position += header;
                    int substream = reader.ReadByte();
                    int channel = reader.ReadByte();
                    reader.ReadByte();
                    reader.ReadByte();
                    if (substream != 0xff || channel != 0x90)
                        throw new InvalidDataException("Formato privado de audio desconhecido.");
                    int samples = checked((int)(end - input.Position));
                    CopyBytes(input, output, samples);
                    bytes += samples;
                }
                input.Position = end;
            }
        }
        if (bytes % 1024 != 0)
            throw new InvalidDataException("PCM do PSS terminou com bloco estereo incompleto.");
        return bytes;
    }
}
'@
}

$null = New-Item -ItemType Directory -Path $Destination -Force
$work = Join-Path $Destination ('.RingOfJudgment-video-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
$disk = [IO.File]::OpenRead($Iso)
try {
    # Entradas MOVIE_00 a MOVIE_18 na tabela PKF da ISO norte-americana SLUS-20347.
    # Cada offset na tabela usa unidades de 16 bytes dentro de MOVIE.PKB.
    $entries = @(809,811,813,815,801,803,805,807,817,818,808,810,812,814,800,802,804,806,816)
    $reader = [IO.BinaryReader]::new($disk, [Text.Encoding]::ASCII, $true)
    $disk.Position = 1469136L * 2048 + 4
    if ($reader.ReadUInt32() -ne 33552) { throw 'A tabela desta ISO nao corresponde a edicao SLUS-20347.' }
    # Converte primeiro a apresentacao do logo e o video de New Game.
    foreach ($number in $MovieNumbers) {
        $name = 'MOVIE_{0:00}' -f $number
        $target = Join-Path $Destination ($name + '.mp4')
        $exists = Test-Path -LiteralPath $target
        if ($exists -and !$ReplaceExisting) { continue }
        $disk.Position = 1469136L * 2048 + 16 + 16 * $entries[$number] + 8
        $offset = [long]$reader.ReadUInt32() * 16
        $length = [long]$reader.ReadUInt32()
        if ($length -lt 4096 -or $offset + $length -gt 1988808708L) { throw "Entrada invalida de $name." }
        $absolute = 137492L * 2048 + $offset
        $disk.Position = $absolute
        if ($reader.ReadUInt32() -ne 0xBA010000L) { throw "Pacote MPEG invalido em $name." }
        $pss = Join-Path $work 'original.pss'
        $pcm = Join-Path $work 'original.pcm'
        $tmp = Join-Path $work 'converted.mp4'
        [RingPss]::Extract($disk, $absolute, $length, $pss)
        $audioBytes = [RingPss]::ExtractPcm($pss, $pcm)
        if ($audioBytes) { [RingPss]::InterleavePssStereo($pcm) }
        $audioOptions = @('-an')
        $videoInput = if ($exists) { $target } else { $pss }
        $inputs = @('-i', $videoInput)
        if ($audioBytes -gt 0) {
            $inputs += @('-f','s16le','-ar','48000','-ac','2','-i',$pcm)
            $audioOptions = @('-map','1:a:0','-c:a','aac','-b:a','320k')
            if ($number -in 0,16,18) { $audioOptions += @('-af','volume=-6dB') }
        }
        $videoOptions = @('-c:v','copy')
        if (!$exists) {
            $videoOptions = @('-vf','setsar=14/15','-c:v','libx264','-preset','veryfast','-crf','19','-pix_fmt','yuv420p','-threads','3','-r','30000/1001')
        }
        & $Ffmpeg -nostdin -hide_banner -loglevel error -y @inputs -map '0:v:0' @audioOptions @videoOptions -movflags '+faststart' $tmp
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $tmp)) { throw "Conversao falhou em $name." }
        if ($exists) {
            [IO.File]::Replace($tmp, $target, [NullString]::Value)
        } else {
            Move-Item -LiteralPath $tmp -Destination $target
        }
        Remove-Item -LiteralPath $pss,$pcm
        Write-Output ("{0}.mp4, PCM original: {1:N1} s" -f $name,($audioBytes / 192000))
    }
} finally {
    $disk.Dispose()
    Get-ChildItem -LiteralPath $work -File | Remove-Item
    Remove-Item -LiteralPath $work
}
