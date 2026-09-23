# One-shot offline rebake: walks the BuiltinCarCylinders.cs heuristic-derived
# sections and assigns EngineConfig per entry by re-matching the carId against
# the chassis + codename + brand+cyl tables (same logic the runtime uses, but
# carId-only — no ui_car.json access needed). Cuts most of the runtime
# second-pass workload by precomputing what we can statically.
#
# Sections processed (any heuristic-derived entry left at Auto):
#   chassis (208)  codename (132)  cylword (7)  desc (38)
#   desc-word (24)  tag (73)
# Already explicit (skipped): chassis-rotary, tag-rotary, rotor-phrase.
#
# Sections we can't fully resolve from carId alone (codename / desc /
# desc-word / tag entries where the layout signal is in the description
# but not the carId) keep cyl-only Auto config — runtime second-pass + a
# fresh probe run will fill those in.

param(
    [string]$Path = "$PSScriptRoot/../src/TrueforceForAll.Plugin/BuiltinCarCylinders.cs"
)

# Chassis patterns paired with EngineConfig. Order matters — more-specific
# patterns first. Mirrors CarCylinderResolver.ChassisLookup so the offline
# rebake matches what runtime would do.
$chassisRules = @(
    # BMW chassis codes — straight-6 Inline by default
    @{ Pat = '\bE3[06]\b|\bE3[06][_\s-]'; Cfg = 'Inline' }
    @{ Pat = '\bE36\b|\bE36[_\s-]';       Cfg = 'Inline' }
    @{ Pat = '\bE46\b|\bE46[_\s-]';       Cfg = 'Inline' }
    @{ Pat = '\bE9[02]\b|\bE9[02][_\s-]'; Cfg = 'Inline' }
    @{ Pat = '\bF8[02]\b|\bF8[02][_\s-]'; Cfg = 'Inline' }
    @{ Pat = '\bG8[02]\b|\bG8[02][_\s-]'; Cfg = 'Inline' }
    # Nissan
    @{ Pat = '\b350Z\b|\bfairlady[_\s]?350|\bz33\b';                       Cfg = 'V60' }
    @{ Pat = '\b370Z\b|\bz34\b';                                            Cfg = 'V60' }
    @{ Pat = '\b300Z\b|\bz3[12]\b';                                         Cfg = 'V60' }
    @{ Pat = '\b240sx\b';                                                   Cfg = 'Inline' }
    @{ Pat = '\bskyline[_\s]?gtr?[_\s]?r3[234]|\bbnr3[24]\b|\bbcnr-?33\b|\br3[234][_\s-]?gtr?\b'; Cfg = 'Inline' }
    @{ Pat = '\b(?:r3[1234]|hr3[12]|hcr3[12])\b';                           Cfg = 'Inline' }
    @{ Pat = '\bs1[345]\b|\bsilvia[_\s]?s1[345]';                           Cfg = 'Inline' }
    @{ Pat = '\b180sx\b|\bsil80\b|\bsileighty\b';                           Cfg = 'Inline' }
    @{ Pat = '\blaurel\b|\bcefiro\b|\bstagea\b';                            Cfg = 'Inline' }
    # Mazda
    @{ Pat = '\bmiata\b|\bmx[-_\s]?5\b|\bmx5\b';                            Cfg = 'Inline' }
    @{ Pat = '\brx[-_\s]?[78]\b';                                           Cfg = 'Rotary' }
    # Subaru / Toyota 86
    @{ Pat = '\bimpreza|\bwrx\b|\bsti\b|\bgrb\b|\bgda\b|\bgdb\b';           Cfg = 'Boxer' }
    @{ Pat = '\bbrz\b|\bgt86\b|\bft86\b|\b86\b';                            Cfg = 'Boxer' }
    # Toyota
    @{ Pat = '\bsupra[_\s]?(?:mk4|a80)|\bjza80\b';                          Cfg = 'Inline' }
    @{ Pat = '\bsupra[_\s]?(?:mk5|a90|gr)\b';                               Cfg = 'Inline' }
    @{ Pat = '\b(?:celica[_\s]?)?supra[_\s]?(?:mk2|a60)|\bma6[01]\b';       Cfg = 'Inline' }
    @{ Pat = '\bae86\b|\bcorolla[_\s]?levin|\btrueno';                      Cfg = 'Inline' }
    @{ Pat = '\bjzx[789]\d\b|\bcresta\b|\bchaser\b|\bsoarer\b|\bmark[_\s]?ii\b'; Cfg = 'Inline' }
    # Mitsubishi
    @{ Pat = '\b(?:lancer[_\s])?evo(?:lution)?[_\s]?(?:[ivx]+|\d+)\b|\bevo[_\s]?[ivx]+\b'; Cfg = 'Inline' }
    # Mini
    @{ Pat = '\bmini[_\s]?(?:cooper|hatch|clubman|countryman)\b|\baustin[_\s]?mini\b'; Cfg = 'Inline' }
    # USDM
    @{ Pat = '\bviper\b|\brt[/_\s-]?10\b';                                  Cfg = 'V90Even' }
    @{ Pat = '\bf-?150\b|\bsilverado\b|\bram[_\s]?(?:1500|2500)\b';         Cfg = 'V8CrossPlane' }
    # Porsche flat-6
    @{ Pat = '\b911\b|\bcayman\b|\bboxster\b|\b993\b|\b996\b|\b997\b|\b991\b|\b992\b|\b964\b|\b930\b'; Cfg = 'Boxer' }
    # Honda
    @{ Pat = '\bs2000\b|\bap[12]\b';                                        Cfg = 'Inline' }
    @{ Pat = '\bcivic\b|\bintegra\b|\btype[-_\s]?r\b';                      Cfg = 'Inline' }
    @{ Pat = '\bnsx\b';                                                     Cfg = 'V60' }
    # Ford / Chevy / Dodge — V8 cross-plane on muscle
    @{ Pat = '\bmustang\b|\bfoxbody\b|\bsn95\b|\bcobra[_\s]?r\b';           Cfg = 'V8CrossPlane' }
    @{ Pat = '\bcamaro\b|\bcorvette\b|\bvette\b|\bc[5678]\b|\bzl1\b|\bz/?28\b'; Cfg = 'V8CrossPlane' }
    @{ Pat = '\bchallenger\b|\bcharger\b|\bhellcat\b|\bredeye\b|\bdemon\b'; Cfg = 'V8CrossPlane' }
    # Honda chassis codes (B/D/K-series I4, all Inline)
    @{ Pat = '\bdc[25]\b|\bek[3-9]\b|\bem1\b|\bef[8-9]\b|\beg[6-9]\b|\bep3\b|\bfd2\b|\bfk8\b|\bfl5\b'; Cfg = 'Inline' }
    # Toyota JZ-engined (JZX/JZA/JZS/JZZ all use 1JZ or 2JZ I6)
    @{ Pat = '\bjzx\d*\b|\bjza\d*\b|\bjzs\d*\b|\bjzz\d*\b';                 Cfg = 'Inline' }
    # Mazda Miata aliases
    @{ Pat = '\beunos\b|\broadster\b|\bna[68]\b|\bnb[68]\b|\bnc[12]\b|\bnd[12]\b'; Cfg = 'Inline' }
    # Kei cars (mostly I3)
    @{ Pat = '\bcopen\b|\bacty\b|\balto\b|\bcappuccino\b|\bbeat\b|\baz-?1\b'; Cfg = 'Inline' }
    # AMG / Mercedes-AMG modern V8s — cross-plane
    @{ Pat = '\bamg[_\s]?gt\b|\bsls\b|\bc63\b|\be63\b|\bs63\b|\bg63\b';      Cfg = 'V8CrossPlane' }
    # Lamborghini chassis tokens (Murcielago / Aventador V12, Gallardo / Huracan V10)
    @{ Pat = '\bmurcielago\b|\baventador\b|\bdiablo\b|\bcountach\b|\bmiura\b|\bsesto\b|\bessenza\b'; Cfg = 'V60' }
    @{ Pat = '\bgallardo\b|\bhuracan\b|\bperformante\b';                    Cfg = 'V90Even' }
    # Ferrari V8 model tokens (all post-1973 are flat-plane)
    @{ Pat = '\b458\b|\b488\b|\bf8\b|\b430\b|\b360\b|\b355\b|\b348\b|\b308\b|\bf40\b|\b288[_\s]?gto\b|\bportofino\b|\bcalifornia\b'; Cfg = 'V8FlatPlane' }
    # Porsche 930 / older 911 variants
    @{ Pat = '\b930\b|\b934\b|\b935\b|\b962\b|\b908\b|\b906\b|\b917\b';      Cfg = 'Boxer' }
    # McLaren chassis (M838/M840 V8 family)
    @{ Pat = '\b570s?\b|\b600lt\b|\b620r\b|\b650s?\b|\b675lt\b|\b720s?\b|\b750s?\b|\b765lt\b|\bmp4-?12c\b|\bsenna\b|\bartura\b'; Cfg = 'V8FlatPlane' }
    # Substring catches (no word boundary) for tokens that frequently appear
    # mid-word in mod carIds (e.g. "scoobys14", "glo93foxbody"). Restricted
    # to brand+chassis pairs that are unambiguous about layout.
    @{ Pat = 'silvia|sileighty|sil80';    Cfg = 'Inline' }       # Nissan Silvia (SR/CA I4)
    @{ Pat = 'skyline|gtr3[234]|gtr_r3'; Cfg = 'Inline' }        # GT-R / Skyline (RB I6 — modern R35 V6 caught earlier)
    @{ Pat = 'shelby|foxbody|cobrar';     Cfg = 'V8CrossPlane' } # Ford Shelby / Mustang variants
    @{ Pat = 'fairlady';                  Cfg = 'V60' }          # Z32/Z33/Z34 (V6 even-fire)
    @{ Pat = 'starion';                   Cfg = 'Inline' }       # Mitsubishi Starion (4G54 I4)
    @{ Pat = 'altezza';                   Cfg = 'Inline' }       # Toyota Altezza (3SGE I4)
    @{ Pat = 'nsx';                       Cfg = 'V60' }          # Honda NSX (C30/C32 V6)
    @{ Pat = 's197|s550|s650';            Cfg = 'V8CrossPlane' } # Mustang chassis codes
    @{ Pat = 'g35|g37|q5[06]';            Cfg = 'V60' }          # Infiniti G/Q (VQ V6)
    @{ Pat = 'singer';                    Cfg = 'Boxer' }        # Singer-era 911 mods
)

# Codename → EngineConfig (subset that resolves layout from carId tokens
# alone — codenames in descriptions aren't seen here).
$codenameRules = @(
    @{ Pat = '\b2JZ|1JZ|7M[-\s]?GTE'; Cfg = 'Inline' }
    @{ Pat = '\bRB2[567]|\bRB30';     Cfg = 'Inline' }
    @{ Pat = '\bM5[02]B|\bS5[024]B|\bN5[24]B|\bB58'; Cfg = 'Inline' }
    @{ Pat = '\b4[AG][-\s]?GE|SR20|CA18|KA24|4G6[34]|K20|K24|F20C|H22A|3SGTE|S14[BE]'; Cfg = 'Inline' }
    @{ Pat = '\bEJ20|EJ25|FA20';      Cfg = 'Boxer' }
    @{ Pat = '\bLS[1-9]|\bLT[1-5]|\bCoyote|\bHEMI|\bHellcat'; Cfg = 'V8CrossPlane' }
    @{ Pat = '\bVoodoo';              Cfg = 'V8FlatPlane' }
    @{ Pat = '\bVQ3[57]|\bVR38';      Cfg = 'V60' }
    @{ Pat = '\bMezger';              Cfg = 'Boxer' }
    @{ Pat = '\b13B|Renesis';         Cfg = 'Rotary' }
    @{ Pat = '\b20B';                 Cfg = 'Rotary' }
    @{ Pat = '\b26B';                 Cfg = 'Rotary' }
    @{ Pat = '\bF13[046]|\bF154|\bF120|\bDFV|\bM83[78]|\bM840'; Cfg = 'V8FlatPlane' }
    @{ Pat = '\bM1[57][89]|\bM177';   Cfg = 'V8CrossPlane' }
    @{ Pat = '\bF14[01]|\bColombo|\bL5(?:39|02|07)'; Cfg = 'V60' }
)

# Brand+cyl rules: applied last as a fallback when no chassis/codename matches
# but the carId reveals brand and we know the layout for that brand+cyl.
function Resolve-BrandCfg {
    param([string]$Hay, [int]$Cyl)
    $h = $Hay.ToLower()
    if ($Cyl -eq 8) {
        if ($h -match '\bferrari\b|\blotus\b|\bmclaren\b|\bmaserati\b') { return 'V8FlatPlane' }
        if ($h -match '\bford\b|\bchevy\b|\bchevrolet\b|\bdodge\b|\bcadillac\b|\bpontiac\b|\bbuick\b|\bram\b|\bamg\b|\bmercedes\b') { return 'V8CrossPlane' }
    }
    if ($Cyl -eq 12) {
        if ($h -match '\bferrari\b|\blamborghini\b|\blambo\b|\bpagani\b|\bamg\b|\bmercedes\b') { return 'V60' }
    }
    if ($Cyl -eq 10) {
        if ($h -match '\blamborghini\b|\blambo\b|\baudi\b') { return 'V90Even' }
    }
    if ($Cyl -eq 4 -or $Cyl -eq 6) {
        if ($h -match '\bsubaru\b') { return 'Boxer' }
        if ($Cyl -eq 6 -and $h -match '\bporsche\b') { return 'Boxer' }
    }
    return $null
}

function Resolve-Cfg {
    param([string]$CarId, [int]$Cyl)
    # Underscores are word chars in regex, so "_e46" never matches \bE46\b.
    # CarIds in AC mods are heavily underscore-delimited; normalize so word
    # boundaries fire the same way they would on the cleaner name+desc text
    # the runtime heuristic actually scans.
    $hay = $CarId -replace '_', ' '
    foreach ($rule in $chassisRules) {
        if ($hay -match $rule.Pat) { return $rule.Cfg }
    }
    foreach ($rule in $codenameRules) {
        if ($hay -match $rule.Pat) { return $rule.Cfg }
    }
    $brand = Resolve-BrandCfg -Hay $hay -Cyl $Cyl
    if ($brand) { return $brand }
    return $null   # leave Auto
}

# Walk the file. For each line that looks like a heuristic-derived entry
# with a bare BuiltinCarSpec(N) — no EngineConfig — try to resolve a config
# from the carId. If found, insert it.
$lines = Get-Content -LiteralPath $Path -Encoding UTF8
$inHeuristicSection = $false
$updatedCount = 0
$out = New-Object 'System.Collections.Generic.List[string]'
foreach ($line in $lines) {
    if ($line -match 'Heuristic-derived') { $inHeuristicSection = $true }
    if ($line -match 'AssettoCorsa is declared|}\s*;\s*$' -and $line -notmatch '\[".*"\]') {
        # End of dictionary — bail out.
    }
    $newLine = $line
    if ($inHeuristicSection -and $line -match '^\s*\["(?<id>[^"]+)"\]\s*=\s*new BuiltinCarSpec\((?<cyl>\d+)\),(?<rest>.*)$') {
        $carId = $Matches['id']
        $cyl = [int]$Matches['cyl']
        $rest = $Matches['rest']
        $cfg = Resolve-Cfg -CarId $carId -Cyl $cyl
        if ($cfg) {
            # Reconstruct line preserving original padding.
            $left = $line.Substring(0, $line.IndexOf('= new BuiltinCarSpec'))
            $newLine = "$left= new BuiltinCarSpec($cyl, EngineConfig.$cfg),$rest"
            $updatedCount++
        }
    }
    $out.Add($newLine)
}
Set-Content -LiteralPath $Path -Value $out -Encoding UTF8
Write-Host "Updated $updatedCount entries with explicit EngineConfig."
