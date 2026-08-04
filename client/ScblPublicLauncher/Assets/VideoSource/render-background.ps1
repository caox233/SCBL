param(
    [Parameter(Mandatory = $true)]
    [string]$FfmpegPath,

    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"

$sourceDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$assetDirectory = Split-Path -Parent $sourceDirectory
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $assetDirectory "launcher_background.mp4"
}

$awayFrame = Join-Path $sourceDirectory "sam-looking-away.png"
$halfTurnFrame = Join-Path $sourceDirectory "sam-half-turn.png"
$frontFrame = Join-Path $assetDirectory "scbl-launcher-background.png"

foreach ($requiredPath in @($FfmpegPath, $awayFrame, $halfTurnFrame, $frontFrame)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required render input not found: $requiredPath"
    }
}

$filter = @"
[0:v]split=2[awayFirstSource][awayLastSource];
[1:v]split=2[halfFirstSource][halfLastSource];
[awayFirstSource]scale=1280:720:flags=lanczos,setsar=1,fps=30,trim=duration=5,setpts=PTS-STARTPTS[awayFirst];
[awayLastSource]scale=1280:720:flags=lanczos,setsar=1,fps=30,trim=duration=5,setpts=PTS-STARTPTS[awayLast];
[halfFirstSource]scale=1280:720:flags=lanczos,setsar=1,fps=30,trim=duration=2,setpts=PTS-STARTPTS[halfFirst];
[halfLastSource]scale=1280:720:flags=lanczos,setsar=1,fps=30,trim=duration=2,setpts=PTS-STARTPTS[halfLast];
[2:v]scale=1280:720:flags=lanczos,setsar=1,fps=30,trim=duration=4,setpts=PTS-STARTPTS[front];
[awayFirst][halfFirst]xfade=transition=fade:duration=1:offset=4[turning];
[turning][front]xfade=transition=fade:duration=1:offset=5[facing];
[facing][halfLast]xfade=transition=fade:duration=1:offset=8[turningBack];
[turningBack][awayLast]xfade=transition=fade:duration=1:offset=9[sequence];
[sequence]scale=1292:727:flags=lanczos,crop=1280:720:x='6+4*sin(2*PI*t/14)':y='3+2*sin(2*PI*t/14)',setsar=1[camera];
color=c=black@0.0:s=1280x720:r=30:d=14,format=rgba,
drawbox=x='mod(70+t*28,1280)':y='mod(-60+t*315,790)-70':w=1:h=38:color=white@0.09:t=fill,
drawbox=x='mod(190+t*22,1280)':y='mod(210+t*360,790)-70':w=1:h=46:color=white@0.08:t=fill,
drawbox=x='mod(315+t*32,1280)':y='mod(430+t*340,790)-70':w=1:h=34:color=white@0.11:t=fill,
drawbox=x='mod(445+t*26,1280)':y='mod(90+t*390,790)-70':w=1:h=51:color=white@0.07:t=fill,
drawbox=x='mod(560+t*34,1280)':y='mod(340+t*325,790)-70':w=1:h=40:color=white@0.10:t=fill,
drawbox=x='mod(675+t*24,1280)':y='mod(570+t*370,790)-70':w=1:h=45:color=white@0.08:t=fill,
drawbox=x='mod(790+t*30,1280)':y='mod(150+t*350,790)-70':w=1:h=36:color=white@0.10:t=fill,
drawbox=x='mod(910+t*27,1280)':y='mod(470+t*405,790)-70':w=1:h=54:color=white@0.07:t=fill,
drawbox=x='mod(1035+t*35,1280)':y='mod(260+t*330,790)-70':w=1:h=41:color=white@0.09:t=fill,
drawbox=x='mod(1160+t*23,1280)':y='mod(620+t*380,790)-70':w=1:h=48:color=white@0.08:t=fill,
gblur=sigma=0.6[rain];
[camera][rain]overlay=shortest=1:format=auto[withRain];
nullsrc=s=180x100:r=30:d=14,format=rgba,geq=r='0':g='120':b='38':a='max(0,52-0.82*hypot(X-90,Y-50))',gblur=sigma=14,
fade=t=in:st=5.2:d=1.0:alpha=1,fade=t=out:st=8.1:d=0.9:alpha=1[opticalGlow];
[withRain][opticalGlow]overlay=x=287:y=90:shortest=1,format=yuv420p[out]
"@ -replace "`r?`n", ""

$arguments = @(
    "-y",
    "-loop", "1", "-framerate", "30", "-i", $awayFrame,
    "-loop", "1", "-framerate", "30", "-i", $halfTurnFrame,
    "-loop", "1", "-framerate", "30", "-i", $frontFrame,
    "-filter_complex", $filter,
    "-map", "[out]",
    "-t", "14",
    "-an",
    "-c:v", "libx264",
    "-preset", "medium",
    "-crf", "22",
    "-pix_fmt", "yuv420p",
    "-movflags", "+faststart",
    $OutputPath
)

& $FfmpegPath @arguments
if ($LASTEXITCODE -ne 0) {
    throw "ffmpeg render failed with exit code $LASTEXITCODE"
}

Get-Item -LiteralPath $OutputPath
