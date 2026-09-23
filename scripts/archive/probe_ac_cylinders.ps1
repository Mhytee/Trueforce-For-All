# Probe installed AC cars and emit cyl + EngineConfig for each.
#
# Originally a one-off cyl-detection probe; now extended to also emit
# EngineConfig (firing-order layout) per car, mirroring the C# resolver's
# DetectEngineConfig second-pass. Generates two artifacts:
#   - scripts/ac_cylinders_probe.csv : full per-car results
#   - scripts/ac_engine_bake.cs.txt  : C# bake fragment ready to merge
#
# Usage:
#   .\scripts\probe_ac_cylinders.ps1                       # default AC path
#   .\scripts\probe_ac_cylinders.ps1 -AcRoot "C:\..."     # custom path
#   .\scripts\probe_ac_cylinders.ps1 -EmitBakeFile        # also write .cs.txt

param(
    [string]$AcRoot = "D:\SteamLibrary\steamapps\common\assettocorsa\content\cars",
    [switch]$EmitBakeFile
)

$root = $AcRoot
$cars = Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue
if (-not $cars) {
    Write-Host "AC content/cars not found at: $root" -ForegroundColor Red
    Write-Host "Pass -AcRoot to point at your install (e.g. C:\Program Files (x86)\Steam\steamapps\common\assettocorsa\content\cars)" -ForegroundColor Yellow
    exit 1
}

# Match tag-style strings: V8, V12, I6, L4, F6, B4, W12, S4 etc.
# Layouts seen in AC tags: V (vee), I/L (inline), F/B (flat/boxer), W, S (single/straight),
# R (rotary, treated specially), H (H-block, rare).
$tagPattern = '^(?<layout>[VILFBWS])(?<count>2|3|4|5|6|8|10|12|16)$'

# Description regex looks for explicit phrases.
# Examples: "V8", "V-8", "5.0L V8", "750-horsepower V12", "3.7-liter V6",
# "inline 6", "inline-six", "flat-6", "boxer 4", "I6 engine", "twin-turbo V8"
$descLayoutPattern = '(?i)\b(V|I|L|F|B|W)[-\s]?(2|3|4|5|6|8|10|12|16)\b'
$descWordPattern = '(?i)\b(?:inline|straight|flat|boxer)[-\s](?:two|three|four|five|six|eight|ten|twelve|2|3|4|5|6|8|10|12)\b'
# "4-cylinder", "6 cylinder", "V8 cylinder layout" etc. The leading char class
# avoids matching "valves per cylinder" (preceded by "per ").
$descCylWordPattern = '(?i)(?<![a-z])(\d{1,2})[-\s]?cylinder\b'
$wordToNum = @{
    'two' = 2; 'three' = 3; 'four' = 4; 'five' = 5; 'six' = 6;
    'eight' = 8; 'ten' = 10; 'twelve' = 12;
}

# Engine codename → cylinder count. These are families known by their
# cylinder layout regardless of chassis. Order matters — longer/more-specific
# names first so "2JZ-GTE" matches before generic "2JZ".
# Sources: well-documented car-enthusiast knowledge of common JDM/USDM swaps.
$engineCodenames = @(
    # JDM straight-six (I6)
    @{ Pattern = '\b2JZ(?:[-\s]?GTE|[-\s]?GE)?\b'; Cylinders = 6 }
    @{ Pattern = '\b1JZ(?:[-\s]?GTE|[-\s]?GE)?\b'; Cylinders = 6 }
    @{ Pattern = '\bRB2[567](?:DET{1,2})?\b';      Cylinders = 6 }
    @{ Pattern = '\bRB30\b';                       Cylinders = 6 }
    @{ Pattern = '\b7M[-\s]?GTE\b';                Cylinders = 6 }
    @{ Pattern = '\bM5[02]B[0-9]+\b';              Cylinders = 6 } # BMW M50/M52
    @{ Pattern = '\bS5[024]B[0-9]+\b';             Cylinders = 6 } # BMW S50/S52/S54
    @{ Pattern = '\bN5[24]B[0-9]+\b';              Cylinders = 6 } # BMW N52/N54
    @{ Pattern = '\bB58\b';                        Cylinders = 6 }
    # JDM straight-four (I4)
    @{ Pattern = '\b4[AG][-\s]?GE\b';              Cylinders = 4 } # Toyota 4A-GE
    @{ Pattern = '\bSR20(?:DET|VE|DE)?\b';         Cylinders = 4 }
    @{ Pattern = '\bCA18(?:DET|DE)?\b';            Cylinders = 4 }
    @{ Pattern = '\bKA24(?:DE|E)?\b';              Cylinders = 4 }
    @{ Pattern = '\b4G6[34]\b';                    Cylinders = 4 } # Mitsubishi 4G63/4G64
    @{ Pattern = '\bK20[ACZ]?\b';                  Cylinders = 4 } # Honda K20
    @{ Pattern = '\bK24[AZ]?\b';                   Cylinders = 4 }
    @{ Pattern = '\bB1[68][AB][0-9]?\b';           Cylinders = 4 } # Honda B-series
    @{ Pattern = '\bF20C\b';                       Cylinders = 4 }
    @{ Pattern = '\bH22A\b';                       Cylinders = 4 }
    @{ Pattern = '\b3SGTE\b';                      Cylinders = 4 }
    @{ Pattern = '\bS14[BE][0-9]+\b';              Cylinders = 4 } # BMW S14 (E30 M3)
    # Subaru flat-four (F4)
    @{ Pattern = '\bEJ20[57]?\b';                  Cylinders = 4 }
    @{ Pattern = '\bEJ25[57]?\b';                  Cylinders = 4 }
    @{ Pattern = '\bFA20\b';                       Cylinders = 4 }
    # USDM V8
    @{ Pattern = '\bLS[1-9]X?\b';                  Cylinders = 8 } # LS1, LS2, LS3, LS7, LSX
    @{ Pattern = '\b\b5\.3[\s-]?LS\b';             Cylinders = 8 }
    @{ Pattern = '\bLT[1-5]\b';                    Cylinders = 8 } # LT1, LT4, etc.
    @{ Pattern = '\bCoyote\b';                     Cylinders = 8 } # Ford 5.0 Coyote
    @{ Pattern = '\bHEMI\b';                       Cylinders = 8 }
    @{ Pattern = '\bHellcat\b';                    Cylinders = 8 }
    # Nissan V6
    @{ Pattern = '\bVQ3[57]\b';                    Cylinders = 6 } # 350Z/G35 VQ35DE, 370Z VQ37VHR
    @{ Pattern = '\bVR38\b';                       Cylinders = 6 } # GT-R R35 (V6 twin-turbo)
    # Porsche flat-6
    @{ Pattern = '\bMezger\b';                     Cylinders = 6 }
)

# Brand+model chassis-code lookup (used as a final fallback). Conservative —
# only entries where the stock engine is unambiguous AND swaps are uncommon
# in AC modding. Users can override anyway.
# Pattern is matched against name OR carId (case-insensitive).
$chassisLookup = @(
    # BMW chassis codes — straight-6 by default, but LS swaps exist (we accept the false positive risk; user can override)
    @{ Pattern = '\bE3[06]\b|\bE3[06][_\s-]'; Cylinders = 6 }  # E30 (most M20/M40 are I6 or I4)
    @{ Pattern = '\bE36\b|\bE36[_\s-]';       Cylinders = 6 }  # E36 — usually I6 (M50/S50)
    @{ Pattern = '\bE46\b|\bE46[_\s-]';       Cylinders = 6 }  # E46 — I6 default, M3 = S54 I6
    @{ Pattern = '\bE9[02]\b|\bE9[02][_\s-]'; Cylinders = 6 }  # E90/E92 — N52/N54 I6, M3 E92 = V8 (false positive)
    @{ Pattern = '\bF8[02]\b|\bF8[02][_\s-]'; Cylinders = 6 }  # F80/F82 M3/M4 — S55 I6
    @{ Pattern = '\bG8[02]\b|\bG8[02][_\s-]'; Cylinders = 6 }  # G80/G82 M3/M4 — S58 I6
    # Nissan
    @{ Pattern = '\b350Z\b|\bfairlady[_\s]?350|\bz33\b'; Cylinders = 6 }  # VQ35
    @{ Pattern = '\b370Z\b|\bz34\b';                     Cylinders = 6 }  # VQ37
    @{ Pattern = '\b300Z\b|\bz3[12]\b';                  Cylinders = 6 }  # VG30
    @{ Pattern = '\b240(?:sx|z)\b';                      Cylinders = 4 }  # KA24 (240SX); 240Z is I6 — kept undetected for safety
    @{ Pattern = '\bskyline[_\s]?gtr?[_\s]?r3[234]|\bbnr3[24]\b|\bbcnr33\b|\b\(bcnr-?33\)|\br3[234][_\s-]?gtr?\b'; Cylinders = 6 }
    @{ Pattern = '\b(?:r3[1234]|hr3[12]|hcr3[12])\b';    Cylinders = 6 }  # Skyline non-GTR sedans (RB-series)
    @{ Pattern = '\bs1[345]\b|\bsilvia[_\s]?s1[345]';    Cylinders = 4 }  # S13/S14/S15 Silvia (CA18/SR20)
    @{ Pattern = '\b180sx\b|\bsil80\b|\bsileighty\b';    Cylinders = 4 }  # CA18/SR20
    @{ Pattern = '\blaurel\b|\bcefiro\b|\bstagea\b';     Cylinders = 6 }  # RB-series Nissan sedans/wagons
    # Mazda
    @{ Pattern = '\bmiata\b|\bmx[-_\s]?5\b|\bmx5\b';     Cylinders = 4 }
    @{ Pattern = '\brx[-_\s]?[78]\b';                    Cylinders = -1 } # rotary
    # Subaru
    @{ Pattern = '\bimpreza|\bwrx\b|\bsti\b|\bgrb\b|\bgda\b|\bgdb\b'; Cylinders = 4 } # F4 boxer
    @{ Pattern = '\bbrz\b|\bgt86\b|\bft86\b|\b86\b';     Cylinders = 4 } # F4 (FA20)
    # Toyota
    @{ Pattern = '\bsupra[_\s]?(?:mk4|a80)|\bjza80\b';   Cylinders = 6 } # 2JZ
    @{ Pattern = '\bsupra[_\s]?(?:mk5|a90|gr)\b';        Cylinders = 6 } # B58
    @{ Pattern = '\b(?:celica[_\s]?)?supra[_\s]?(?:mk2|a60)|\bma6[01]\b'; Cylinders = 6 }  # 5M-GE I6
    @{ Pattern = '\bae86\b|\bcorolla[_\s]?levin|\btrueno'; Cylinders = 4 } # 4A-GE
    @{ Pattern = '\bjzx[789]\d\b|\bcresta\b|\bchaser\b|\bsoarer\b|\bmark[_\s]?ii\b'; Cylinders = 6 }  # 1JZ/2JZ
    # Mitsubishi
    @{ Pattern = '\b(?:lancer[_\s])?evo(?:lution)?[_\s]?(?:[ivx]+|\d+)\b|\bevo[_\s]?[ivx]+\b'; Cylinders = 4 } # 4G63
    # Misc British / European
    @{ Pattern = '\bmini[_\s]?(?:cooper|hatch|clubman|countryman)\b|\baustin[_\s]?mini\b'; Cylinders = 4 }
    # Misc USDM
    @{ Pattern = '\bviper\b|\brt[/_\s-]?10\b';            Cylinders = 10 }
    @{ Pattern = '\bf-?150\b|\bsilverado\b|\bram[_\s]?(?:1500|2500)\b'; Cylinders = 8 }
    # Porsche
    @{ Pattern = '\b911\b|\bcayman\b|\bboxster\b|\b993\b|\b996\b|\b997\b|\b991\b|\b992\b|\b964\b'; Cylinders = 6 } # F6
    # Honda
    @{ Pattern = '\bs2000\b|\bap[12]\b';                 Cylinders = 4 } # F20C/F22C
    @{ Pattern = '\bcivic\b|\bintegra\b|\btype[-_\s]?r\b'; Cylinders = 4 }
    @{ Pattern = '\bnsx\b';                              Cylinders = 6 } # original NSX = C30A V6
    # Ford / Chevy / Dodge — usually V8 in AC mods, but very mixed
    @{ Pattern = '\bmustang\b';                          Cylinders = 8 } # majority V8, ecoboost is I4
    @{ Pattern = '\bcamaro\b|\bcorvette\b|\bvette\b|\bc[5678]\b'; Cylinders = 8 }
    @{ Pattern = '\bchallenger\b|\bcharger\b';           Cylinders = 8 }
)

# EngineConfig pattern tables. Mirror CarCylinderResolver.cs: each codename /
# chassis carries a layout, plus brand+cyl combos as a fallback. Output values
# are the C# enum-name strings ("V8FlatPlane" etc.) for direct paste into bake.
$codenameToConfig = @(
    @{ Pat = '\bLS[1-9]X?\b';                    Cfg = 'V8CrossPlane' }
    @{ Pat = '\bLT[1-5]\b';                      Cfg = 'V8CrossPlane' }
    @{ Pat = '\bCoyote\b';                       Cfg = 'V8CrossPlane' }
    @{ Pat = '\bHEMI\b';                         Cfg = 'V8CrossPlane' }
    @{ Pat = '\bHellcat\b';                      Cfg = 'V8CrossPlane' }
    @{ Pat = '\bM1[57][89]\b|\bM177\b';          Cfg = 'V8CrossPlane' }
    @{ Pat = '\bF13[046][A-Z]?\b';               Cfg = 'V8FlatPlane' }
    @{ Pat = '\bF154[A-Z]?\b';                   Cfg = 'V8FlatPlane' }
    @{ Pat = '\bF120[A-Z]?\b';                   Cfg = 'V8FlatPlane' }
    @{ Pat = '\bDFV\b';                          Cfg = 'V8FlatPlane' }
    @{ Pat = '\bVoodoo\b';                       Cfg = 'V8FlatPlane' }
    @{ Pat = '\bM83[78]T?\b|\bM840T?\b';         Cfg = 'V8FlatPlane' }
    @{ Pat = '\bMezger\b';                       Cfg = 'Boxer' }
    @{ Pat = '\bEJ20[57]?\b|\bEJ25[57]?\b|\bFA20\b'; Cfg = 'Boxer' }
    @{ Pat = '\b13B(?:[-\s]?(?:REW|MSP|T))?\b';  Cfg = 'Rotary' }
    @{ Pat = '\bRenesis\b';                      Cfg = 'Rotary' }
    @{ Pat = '\b20B(?:[-\s]?REW)?\b|\b26B\b';    Cfg = 'Rotary' }
    @{ Pat = '\bF14[01]\b|\bColombo\b';          Cfg = 'V60' }
    @{ Pat = '\bL5(?:39|02|07)\b';               Cfg = 'V60' }
    @{ Pat = '\bVQ3[57]\b|\bVR38\b';             Cfg = 'V60' }
)

$chassisToConfig = @(
    @{ Pat = '\bE3[06]\b|\bE3[06][_\s-]|\bE36\b|\bE36[_\s-]|\bE46\b|\bE46[_\s-]|\bE9[02]\b|\bE9[02][_\s-]|\bF8[02]\b|\bF8[02][_\s-]|\bG8[02]\b|\bG8[02][_\s-]'; Cfg = 'Inline' }
    @{ Pat = '\b350Z\b|\bfairlady|\bz3[1234]\b|\b370Z\b';                Cfg = 'V60' }
    @{ Pat = '\b300Z\b';                                                  Cfg = 'V60' }
    @{ Pat = '\b240sx\b|\b180sx\b|\bsil80\b|\bsileighty\b|\bs1[345]\b|\bsilvia';  Cfg = 'Inline' }
    @{ Pat = '\bskyline|\bbnr3[24]\b|\bbcnr-?33\b|\br3[234][_\s-]?gtr?\b|\b(?:r3[1234]|hr3[12]|hcr3[12])\b'; Cfg = 'Inline' }
    @{ Pat = '\blaurel\b|\bcefiro\b|\bstagea\b';                          Cfg = 'Inline' }
    @{ Pat = '\bmiata\b|\bmx[-_\s]?5\b|\beunos\b|\bna[68]\b|\bnb[68]\b|\bnc[12]\b|\bnd[12]\b'; Cfg = 'Inline' }
    @{ Pat = '\brx[-_\s]?[78]\b';                                         Cfg = 'Rotary' }
    @{ Pat = '\bimpreza|\bwrx\b|\bsti\b|\bgrb\b|\bgda\b|\bgdb\b|\bbrz\b|\bgt86\b|\bft86\b'; Cfg = 'Boxer' }
    @{ Pat = '\bsupra[_\s]?(?:mk[245]|a[689]0|gr)|\bjza80\b|\bma6[01]\b|\bjzx[789]\d|\bcresta\b|\bchaser\b|\bsoarer\b|\bmark[_\s]?ii'; Cfg = 'Inline' }
    @{ Pat = '\bae86\b|\bcorolla[_\s]?levin|\btrueno|\baltezza\b';        Cfg = 'Inline' }
    @{ Pat = '\bevo(?:lution)?[_\s]?(?:[ivx]+|\d+)|\bstarion\b';          Cfg = 'Inline' }
    @{ Pat = '\bmini[_\s]?(?:cooper|hatch|clubman|countryman)|\baustin[_\s]?mini'; Cfg = 'Inline' }
    @{ Pat = '\bviper\b|\brt[/_\s-]?10\b';                                Cfg = 'V90Even' }
    @{ Pat = '\bf-?150\b|\bsilverado\b|\bram[_\s]?(?:1500|2500)';         Cfg = 'V8CrossPlane' }
    @{ Pat = '\b911\b|\bcayman\b|\bboxster\b|\b993\b|\b996\b|\b997\b|\b991\b|\b992\b|\b964\b|\b930\b|\b934\b|\b935\b|\b962\b|\b908\b|\b906\b|\b917\b|\bsinger\b'; Cfg = 'Boxer' }
    @{ Pat = '\bs2000\b|\bap[12]\b';                                      Cfg = 'Inline' }
    @{ Pat = '\bcivic\b|\bintegra\b|\btype[-_\s]?r\b|\bdc[25]\b|\bek[3-9]\b|\bem1\b|\bef[8-9]\b|\beg[6-9]\b|\bep3\b|\bfd2\b|\bfk8\b|\bfl5\b'; Cfg = 'Inline' }
    @{ Pat = '\bnsx\b';                                                   Cfg = 'V60' }
    @{ Pat = '\bmustang\b|\bfoxbody\b|\bsn95\b|\bs197\b|\bs550\b|\bs650\b|\bshelby\b'; Cfg = 'V8CrossPlane' }
    @{ Pat = '\bcamaro\b|\bcorvette\b|\bvette\b|\bc[5678]\b|\bzl1\b|\bz/?28\b'; Cfg = 'V8CrossPlane' }
    @{ Pat = '\bchallenger\b|\bcharger\b|\bdemon\b';                      Cfg = 'V8CrossPlane' }
    @{ Pat = '\bamg[_\s]?gt\b|\bsls\b|\bc63\b|\be63\b|\bs63\b|\bg63\b';   Cfg = 'V8CrossPlane' }
    @{ Pat = '\bmurcielago\b|\baventador\b|\bdiablo\b|\bcountach\b|\bmiura\b|\bsesto\b|\bessenza\b'; Cfg = 'V60' }
    @{ Pat = '\bgallardo\b|\bhuracan\b|\bperformante\b';                  Cfg = 'V90Even' }
    @{ Pat = '\b458\b|\b488\b|\bf8\b|\b430\b|\b360\b|\b355\b|\b348\b|\b308\b|\bf40\b|\b288[_\s]?gto\b|\bportofino\b|\bcalifornia\b'; Cfg = 'V8FlatPlane' }
    @{ Pat = '\b570s?\b|\b600lt\b|\b620r\b|\b650s?\b|\b675lt\b|\b720s?\b|\b750s?\b|\b765lt\b|\bmp4-?12c\b|\bsenna\b|\bartura\b|\bp1\b'; Cfg = 'V8FlatPlane' }
    @{ Pat = '\bjzs\d*|\bjza\d*';                                          Cfg = 'Inline' }
    @{ Pat = '\bg35\b|\bg37\b|\bq5[06]\b';                                 Cfg = 'V60' }
)

# Layout words appearing directly in tags or description: "flat-plane V8",
# "cross-plane crank", "boxer six", "rotary engine".
function Resolve-FromLayoutWords {
    param([string]$Hay, [string[]]$Tags)
    if ($Tags) {
        foreach ($t in $Tags) {
            $tt = "$t".Trim()
            if ($tt -match '(?i)^flat[-\s]?plane$')  { return @{ Cfg = 'V8FlatPlane';  Src = 'tag' } }
            if ($tt -match '(?i)^cross[-\s]?plane$') { return @{ Cfg = 'V8CrossPlane'; Src = 'tag' } }
            if ($tt -match '(?i)^boxer$')            { return @{ Cfg = 'Boxer';        Src = 'tag' } }
            if ($tt -match '(?i)^(rotary|wankel)$')  { return @{ Cfg = 'Rotary';       Src = 'tag' } }
        }
    }
    if ($Hay -match '(?i)\bflat[-\s]?plane\b')  { return @{ Cfg = 'V8FlatPlane';  Src = 'desc-flat-plane' } }
    if ($Hay -match '(?i)\bcross[-\s]?plane\b') { return @{ Cfg = 'V8CrossPlane'; Src = 'desc-cross-plane' } }
    return $null
}

# Brand+cyl inference. Conservative: only fires when both signals are present
# and combine cleanly into a known layout for that brand.
function Resolve-FromBrandCyl {
    param([string]$Hay, [int]$Cyl)
    if ($null -eq $Cyl -or $Cyl -le 0) { return $null }
    $h = $Hay.ToLower()
    if ($Cyl -eq 8) {
        if ($h -match '\bferrari\b|\blotus\b|\bmclaren\b|\bmaserati\b') { return @{ Cfg = 'V8FlatPlane';  Src = 'brand' } }
        if ($h -match '\bford\b|\bchevy\b|\bchevrolet\b|\bdodge\b|\bcadillac\b|\bpontiac\b|\bbuick\b|\bram\b|\bamg\b|\bmercedes\b') { return @{ Cfg = 'V8CrossPlane'; Src = 'brand' } }
    }
    if ($Cyl -eq 12) {
        if ($h -match '\bferrari\b|\blamborghini\b|\blambo\b|\bpagani\b|\bamg\b|\bmercedes\b|\baston\b|\brolls\b') { return @{ Cfg = 'V60'; Src = 'brand' } }
    }
    if ($Cyl -eq 10) {
        if ($h -match '\blamborghini\b|\blambo\b|\baudi\b|\blexus\b') { return @{ Cfg = 'V90Even'; Src = 'brand' } }
    }
    if ($Cyl -eq 4 -or $Cyl -eq 6) {
        if ($h -match '\bsubaru\b') { return @{ Cfg = 'Boxer'; Src = 'brand' } }
        if ($Cyl -eq 6 -and $h -match '\bporsche\b') { return @{ Cfg = 'Boxer'; Src = 'brand' } }
    }
    return $null
}

function Resolve-EngineConfig {
    param([string]$Hay, [string[]]$Tags, [int]$Cyl)
    # Rotary: special case driven by cyl signal already if -1 sentinel used.
    $w = Resolve-FromLayoutWords -Hay $Hay -Tags $Tags
    if ($w) { return $w }
    foreach ($r in $codenameToConfig) {
        if ([regex]::IsMatch($Hay, $r.Pat, 'IgnoreCase')) {
            return @{ Cfg = $r.Cfg; Src = 'codename' }
        }
    }
    foreach ($r in $chassisToConfig) {
        if ([regex]::IsMatch($Hay, $r.Pat, 'IgnoreCase')) {
            return @{ Cfg = $r.Cfg; Src = 'chassis' }
        }
    }
    $brand = Resolve-FromBrandCyl -Hay $Hay -Cyl $Cyl
    if ($brand) { return $brand }
    return $null
}

$stats = @{
    Total              = 0
    HasUiCar           = 0
    DetectedByTag      = 0
    DetectedByDesc     = 0
    DetectedByCylWord  = 0
    DetectedByCodename = 0
    DetectedByChassis  = 0
    Detected           = 0
    Rotary             = 0
    Electric           = 0
    Disagreed          = 0
    ConfigDetected     = 0
}

$results = @()
$disagreements = @()

foreach ($car in $cars) {
    $stats.Total++

    $uiPath = Join-Path $car.FullName "ui\ui_car.json"
    if (-not (Test-Path $uiPath)) { continue }
    $stats.HasUiCar++

    try {
        # AC's ui_car.json sometimes has stray bytes — read raw and try parse.
        $raw = Get-Content -Raw -LiteralPath $uiPath -ErrorAction Stop
        # Strip BOM if present
        if ($raw.Length -gt 0 -and $raw[0] -eq [char]0xFEFF) { $raw = $raw.Substring(1) }
        $ui = $raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        continue
    }

    $cylFromTag = $null
    $layoutFromTag = $null

    if ($ui.tags) {
        foreach ($tag in $ui.tags) {
            if ($null -eq $tag) { continue }
            $t = "$tag"
            # Special cases
            if ($t -match '(?i)^rotary$|^wankel$') {
                $stats.Rotary++
                $cylFromTag = -1  # sentinel for rotary
                break
            }
            if ($t -match '(?i)^electric$|^ev$|^bev$') {
                $stats.Electric++
                $cylFromTag = -2
                break
            }
            if ($t -match $tagPattern) {
                $cylFromTag = [int]$Matches.count
                $layoutFromTag = $Matches.layout
                break
            }
        }
    }

    $cylFromDesc = $null
    $layoutFromDesc = $null
    $cylFromCylWord = $null
    $cylFromCodename = $null
    $codenameMatched = $null

    $desc = ""
    if ($ui.description) {
        $desc = "$($ui.description)" -replace '&[a-z]+;', ' '

        # Layout+number patterns (V8, I6, etc.) with per-letter plausibility
        # filter to suppress false positives: "V2" / "V3" almost always mean
        # "version N" of a mod, not a V-twin / V-three engine. Mirror the
        # C# resolver's plausibility table so probe and runtime agree.
        $m = [regex]::Match($desc, $descLayoutPattern)
        while ($m.Success) {
            $layout = $m.Groups[1].Value.ToUpper()
            $count = [int]$m.Groups[2].Value
            $plausible = $false
            switch ($layout) {
                'V' { $plausible = $count -in @(4,6,8,10,12,16) }
                'I' { $plausible = $count -in @(3,4,5,6,8) }
                'L' { $plausible = $count -in @(4,5,6,8) }
                'F' { $plausible = $count -in @(4,6,8,12) }
                'B' { $plausible = $count -in @(4,6) }
                'W' { $plausible = $count -in @(12,16) }
            }
            if ($plausible) {
                $cylFromDesc = $count
                $layoutFromDesc = $layout
                break
            }
            $m = $m.NextMatch()
        }

        if ($null -eq $cylFromDesc) {
            $m = [regex]::Match($desc, $descWordPattern)
            if ($m.Success) {
                $word = $m.Value.ToLower() -replace '[-\s]', ' '
                foreach ($key in $wordToNum.Keys) {
                    if ($word -match "\b$key\b") { $cylFromDesc = $wordToNum[$key]; break }
                }
                if ($null -eq $cylFromDesc -and $word -match '\b(\d+)\b') { $cylFromDesc = [int]$Matches[1] }
            }
        }

        # "X-cylinder" / "X cylinder" pattern — strong signal
        $m = [regex]::Match($desc, $descCylWordPattern)
        if ($m.Success) {
            $cylFromCylWord = [int]$m.Groups[1].Value
        }
    }

    # Combined search corpus for codenames and chassis lookups: name + tags + desc
    $haystack = ""
    if ($ui.name) { $haystack += " " + $ui.name }
    if ($ui.tags) { $haystack += " " + ($ui.tags -join " ") }
    $haystack += " " + $desc
    $haystack += " " + $car.Name  # carId itself

    foreach ($cn in $engineCodenames) {
        if ([regex]::IsMatch($haystack, $cn.Pattern)) {
            $cylFromCodename = $cn.Cylinders
            $codenameMatched = $cn.Pattern
            break
        }
    }

    $cylFromChassis = $null
    foreach ($ch in $chassisLookup) {
        if ([regex]::IsMatch($haystack, "(?i)$($ch.Pattern)")) {
            $cylFromChassis = $ch.Cylinders
            break
        }
    }

    # Priority order: explicit numeric (cyl word) > tag layout > desc layout > codename > chassis
    $detected = $null
    $source = $null
    if ($null -ne $cylFromCylWord -and $cylFromCylWord -ge 2 -and $cylFromCylWord -le 16) {
        $detected = $cylFromCylWord
        $source = "cylword:$cylFromCylWord"
        $stats.DetectedByCylWord++
    } elseif ($null -ne $cylFromTag -and $cylFromTag -gt 0) {
        $detected = $cylFromTag
        $source = "tag:$layoutFromTag$cylFromTag"
        $stats.DetectedByTag++
    } elseif ($null -ne $cylFromDesc) {
        $detected = $cylFromDesc
        $source = "desc:$layoutFromDesc$cylFromDesc"
        $stats.DetectedByDesc++
    } elseif ($null -ne $cylFromCodename -and $cylFromCodename -gt 0) {
        $detected = $cylFromCodename
        $source = "codename:$codenameMatched"
        $stats.DetectedByCodename++
    } elseif ($null -ne $cylFromChassis -and $cylFromChassis -gt 0) {
        $detected = $cylFromChassis
        $source = "chassis"
        $stats.DetectedByChassis++
    } elseif ($cylFromTag -in @(-1, -2) -or $cylFromChassis -eq -1) {
        $detected = if ($cylFromTag -eq -2) { -2 } else { -1 }
        $source = if ($detected -eq -1) { "rotary" } else { "electric" }
    }

    if ($null -ne $detected) {
        $stats.Detected++
    }

    if ($cylFromTag -gt 0 -and $cylFromDesc -gt 0 -and $cylFromTag -ne $cylFromDesc) {
        $stats.Disagreed++
        $disagreements += [pscustomobject]@{
            CarId = $car.Name
            TagSays = "$layoutFromTag$cylFromTag"
            DescSays = "$layoutFromDesc$cylFromDesc"
        }
    }

    # EngineConfig second-pass: only when we have a real cyl (not rotary
    # or electric sentinels — rotary already implies Rotary config; EVs
    # ignore EngineConfig downstream).
    $cfg = $null
    $cfgSrc = $null
    if ($null -ne $detected -and $detected -gt 0) {
        $cfgHit = Resolve-EngineConfig -Hay $haystack -Tags $ui.tags -Cyl $detected
        if ($cfgHit) {
            $cfg = $cfgHit.Cfg
            $cfgSrc = $cfgHit.Src
            $stats.ConfigDetected++
        }
    } elseif ($detected -eq -1) {
        # Rotary sentinel: layout is implied
        $cfg = 'Rotary'
        $cfgSrc = 'rotary-sentinel'
        $stats.ConfigDetected++
    }

    $results += [pscustomobject]@{
        CarId         = $car.Name
        Cylinders     = $detected
        Source        = $source
        EngineConfig  = $cfg
        ConfigSource  = $cfgSrc
    }
}

Write-Host ""
Write-Host "=== AC Cylinder Detection Probe ===" -ForegroundColor Cyan
Write-Host ("Total cars scanned:      {0}" -f $stats.Total)
Write-Host ("Has ui_car.json:         {0}  ({1:p1})" -f $stats.HasUiCar, ($stats.HasUiCar / [math]::Max($stats.Total,1)))
Write-Host ("Detected (any source):   {0}  ({1:p1})" -f $stats.Detected, ($stats.Detected / [math]::Max($stats.Total,1)))
Write-Host ("  via cylinder word:     {0}" -f $stats.DetectedByCylWord)
Write-Host ("  via tags:              {0}" -f $stats.DetectedByTag)
Write-Host ("  via description:       {0}" -f $stats.DetectedByDesc)
Write-Host ("  via engine codename:   {0}" -f $stats.DetectedByCodename)
Write-Host ("  via chassis lookup:    {0}" -f $stats.DetectedByChassis)
Write-Host ("  rotary:                {0}" -f $stats.Rotary)
Write-Host ("  electric:              {0}" -f $stats.Electric)
Write-Host ("Disagreements (tag/desc):{0}" -f $stats.Disagreed)
Write-Host ""

if ($disagreements.Count -gt 0) {
    Write-Host "Disagreements (first 20):" -ForegroundColor Yellow
    $disagreements | Select-Object -First 20 | Format-Table -AutoSize
}

# Distribution of detected cylinder counts
Write-Host "Detected cylinder distribution:" -ForegroundColor Cyan
$results | Where-Object { $_.Cylinders -gt 0 } |
    Group-Object Cylinders | Sort-Object @{Expression={[int]$_.Name}} |
    Format-Table @{n='Cylinders';e={$_.Name}}, Count -AutoSize

# Sample undetected
$undetected = $results | Where-Object { $null -eq $_.Cylinders }
Write-Host ("Undetected sample (first 30 of {0}):" -f $undetected.Count) -ForegroundColor Yellow
$undetected | Select-Object -First 30 -ExpandProperty CarId

# EngineConfig coverage breakdown
Write-Host ("EngineConfig detected:    {0}  ({1:p1} of cyl-detected)" -f $stats.ConfigDetected, ($stats.ConfigDetected / [math]::Max($stats.Detected,1))) -ForegroundColor Cyan
$results | Where-Object { $_.EngineConfig } |
    Group-Object EngineConfig | Sort-Object @{Expression={[int]$_.Count}; Descending=$true} |
    Format-Table @{n='EngineConfig';e={$_.Name}}, Count -AutoSize

# Save full results to CSV for inspection
$csvPath = Join-Path $PSScriptRoot "ac_cylinders_probe.csv"
$results | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8
Write-Host ""
Write-Host "Full results written to: $csvPath" -ForegroundColor Green

# Optional: emit a C# bake fragment grouped by source. Pasteable directly
# into BuiltinCarCylinders.cs or merged via a follow-up script.
if ($EmitBakeFile) {
    $bakePath = Join-Path $PSScriptRoot "ac_engine_bake.cs.txt"
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("// Auto-generated by probe_ac_cylinders.ps1. Do not edit by hand;")
    [void]$sb.AppendLine("// re-run the probe instead. Manual Kunos entries (in the dictionary")
    [void]$sb.AppendLine("// before the heuristic-derived sections) take precedence and are not")
    [void]$sb.AppendLine("// regenerated here.")
    [void]$sb.AppendLine("//")
    [void]$sb.AppendLine(("// Generated: {0}  Probe input: {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm'), $root))
    [void]$sb.AppendLine()

    # Group by source (chassis, codename, etc.) so the bake stays organized.
    $bySource = $results |
        Where-Object { $null -ne $_.Cylinders -and $_.Cylinders -gt 0 } |
        Group-Object { ($_.Source -split ':')[0] }

    foreach ($g in $bySource | Sort-Object Name) {
        [void]$sb.AppendLine(("// ----- {0} ({1} entries) -----" -f $g.Name, $g.Count))
        foreach ($row in ($g.Group | Sort-Object CarId)) {
            $padded = '"' + $row.CarId + '"'
            $padding = ' ' * [math]::Max(1, 50 - $padded.Length)
            $cyl = $row.Cylinders
            if ($row.EngineConfig) {
                [void]$sb.AppendLine(("[{0}]{1}= new BuiltinCarSpec({2}, EngineConfig.{3})," -f $padded, $padding, $cyl, $row.EngineConfig))
            } else {
                [void]$sb.AppendLine(("[{0}]{1}= new BuiltinCarSpec({2})," -f $padded, $padding, $cyl))
            }
        }
        [void]$sb.AppendLine()
    }
    Set-Content -LiteralPath $bakePath -Value $sb.ToString() -Encoding utf8
    Write-Host "Bake fragment written to: $bakePath" -ForegroundColor Green
}
