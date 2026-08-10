#Requires -Version 5.1
<#
.SYNOPSIS
    Downloads the face detector used by webcam head tracking.

.DESCRIPTION
    YuNet, from OpenCV's model zoo. About 230 KB, CPU-only, and the same model
    OpenCV ships in its own samples.

    It must come from media.githubusercontent.com, NOT raw.githubusercontent.com.
    The file is stored with Git-LFS, and raw. serves the LFS *pointer* -- a
    131-byte text file -- with a perfectly good HTTP 200. Downloading that
    succeeds, and the failure only shows up much later as an unreadable ONNX
    parse error from OpenCV. The size check below is the guard.

.PARAMETER Force
    Re-download even if the model is already present.
#>
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$dest     = Join-Path $repoRoot 'models'
$model    = Join-Path $dest 'face_detection_yunet.onnx'
$landmark = Join-Path $dest 'face_landmark_peppa_wutz.onnx'

# peppa_wutz, a 68-point landmarker from FaceFusion's model set.
#
# Why 68 points at all: five cannot measure pitch. The nose tip is the only
# out-of-plane point of the five and the worst localised of them -- eye and
# mouth corners are corners, a nose tip is a smooth surface -- so pitch rides
# the noisiest thing in the set. 68 bring the jawline and chin, which give
# pitch a baseline as solid as the one yaw gets from the eyes.
#
# Why this one and not 2DFAN4, which was here first: 2DFAN4 is 93 MB and takes
# 310 ms per call on an eight-core desktop, 438 ms on the integrated-graphics
# machine this was tested on -- about 3 fps, while competing with video decode.
# peppa_wutz is 13 MB and 28.6 ms for the same [1,3,256,256] in, [1,68,3] out.
# Ten times faster and seven times smaller for a job that needs six of the
# points.
$landmarkUrl = 'https://github.com/facefusion/facefusion-assets/releases/download/models-3.0.0/peppa_wutz.onnx'

# Pinned to a dated release rather than a moving branch: a silently updated
# detector would change tracking behaviour with no commit to point at.
$url = 'https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx'

New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Not an early return on "already present": there is a second model below, and
# skipping the first one used to exit the whole script and silently never
# install the landmarker.
$haveDetector = (Test-Path $model) -and -not $Force -and (Get-Item $model).Length -gt 50000

if ($haveDetector) {
    Write-Host "skip (exists): $model  ($([int]((Get-Item $model).Length / 1024)) KB)"
} else {
    Write-Host "get  YuNet face detector"
    Invoke-WebRequest -Uri $url -OutFile $model -UseBasicParsing

    $size = (Get-Item $model).Length
    if ($size -lt 50000) {
        Remove-Item $model -Force
        throw "Downloaded only $size bytes -- that is the Git-LFS pointer, not the model. " +
              "Check that the URL host is media.githubusercontent.com."
    }
    Write-Host "models: detector installed ($([int]($size / 1024)) KB)"
}

# ------------------------------------------------------------ landmarker ---

if ((Test-Path $landmark) -and -not $Force -and (Get-Item $landmark).Length -gt 8MB) {
    Write-Host "skip (exists): $landmark"
} else {
    Write-Host 'get  68-point landmarker (13 MB)'
    Invoke-WebRequest -Uri $landmarkUrl -OutFile $landmark -UseBasicParsing

    $lsize = (Get-Item $landmark).Length
    if ($lsize -lt 8MB) {
        Remove-Item $landmark -Force
        throw "Landmarker downloaded only $lsize bytes, which is not the model."
    }
    Write-Host "models: landmarker installed ($([int]($lsize / 1MB)) MB)"
}
Write-Host ''
Write-Host 'Check it with:  VRFlatPlayer --camera-preview'

