param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
# Original community mark: a bridge and two network endpoints. No Google artwork.
$images = @()
foreach ($side in @(16,32,48,64,128,256)) {
    $bitmap = New-Object Drawing.Bitmap($side,$side)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::FromArgb(255,18,27,48))
    $pen = New-Object Drawing.Pen([Drawing.Color]::FromArgb(255,88,207,238),([single]($side*0.09)))
    $pen.StartCap=[Drawing.Drawing2D.LineCap]::Round; $pen.EndCap=[Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($pen,([single]($side*.23)),([single]($side*.72)),([single]($side*.5)),([single]($side*.27)))
    $graphics.DrawLine($pen,([single]($side*.5)),([single]($side*.27)),([single]($side*.77)),([single]($side*.72)))
    $graphics.DrawLine($pen,([single]($side*.36)),([single]($side*.57)),([single]($side*.64)),([single]($side*.57)))
    $brush=New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(255,226,242,255))
    foreach($x in @(.14,.70)){$graphics.FillEllipse($brush,([single]($side*$x)),([single]($side*.65)),([single]($side*.16)),([single]($side*.16)))}
    $memory=New-Object IO.MemoryStream; $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
    $images+=,[pscustomobject]@{Side=$side;Bytes=$memory.ToArray()}
    $memory.Dispose();$brush.Dispose();$pen.Dispose();$graphics.Dispose();$bitmap.Dispose()
}
$stream=[IO.File]::Create($Destination);$writer=New-Object IO.BinaryWriter($stream)
try {
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$images.Count)
    $offset=6+16*$images.Count
    foreach($image in $images){$size=if($image.Side -eq 256){0}else{$image.Side};$writer.Write([byte]$size);$writer.Write([byte]$size);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$image.Bytes.Length);$writer.Write([uint32]$offset);$offset+=$image.Bytes.Length}
    foreach($image in $images){$writer.Write([byte[]]$image.Bytes)}
} finally {$writer.Dispose();$stream.Dispose()}
