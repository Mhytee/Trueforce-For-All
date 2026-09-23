# Second-pass rebake: aggressive patterns to catch the long tail of bare
# heuristic entries (~173 cars after probe + first rebake). Higher false-
# positive risk than the first pass, but the alternative is leaving these
# at Auto fallback and they're unlikely to be wronger than what Auto picks.
#
# Strategy: assume Auto's cyl-count default for the common case (V8 ->
# CrossPlane, V12 -> V60, etc.) and only EXPLICITLY bake when we have a
# carId signal that contradicts the default OR confirms a specific layout.
#
# Idempotent: only edits BuiltinCarSpec(N) entries with no second arg.

param(
    [string]$Path = "$PSScriptRoot/../src/TrueforceForAll.Plugin/BuiltinCarCylinders.cs"
)

# Per-cyl pattern tables. First match wins.
$rules4 = @(
    @{ Pat = 'gr86|brz|ft86|gt86|toyota_86|sub86|fa20|ej20|ej25|impreza|wrx|sti|forester|legacy|outback'; Cfg = 'Boxer' }
    @{ Pat = 'fc3s|fc[_\s]?rx|rx[-_\s]?7|rx[-_\s]?8|13b|renesis'; Cfg = 'Rotary' }
    @{ Pat = '.*';  Cfg = 'Inline' }   # everything else cyl=4 -> Inline
)
$rules3 = @(
    @{ Pat = '.*';  Cfg = 'Inline' }   # all I3 (Yaris, Copen, Beat, Mirage etc.)
)
$rules5 = @(
    @{ Pat = '.*';  Cfg = 'Inline' }   # all I5
)
$rules6 = @(
    # BMW I6 chassis tokens (E36/E46/E92/F8x/G8x; M2/M3/M4/M6/M340; X-series M).
    # f8x / g8x match for the literal "x" in carIds like "simhq_f8x".
    @{ Pat = '\be[3-9]\d|\bf[2-9]\d|\bg[2-9]\d|\bm2\b|\bm3\b|\bm4\b|\bm6\b|m340|m240|x3m|x5m|x6m|x7m|csi\b|f8[\dx]|g8[\dx]'; Cfg = 'Inline' }
    # 2JZ / 1JZ / RB-engined (Toyota Supra MKii-iV, Aristo, Soarer, Mark II, Cresta, Chaser, JZX*, Skyline pre-R35)
    @{ Pat = 'supra|aristo|soarer|chaser|jzx|jzs|jza|er34|stagea|verossa|laurel|cefiro|skyline|rbs13|rbs14|rbs15|gs300|sc300|is300|altezza|\bcrown\b|\bma[678]0\b|celicc|\ba[678]0\b'; Cfg = 'Inline' }
    # GT-R R35 / Z34 / Z32 / 350Z / 370Z / G35 / G37 -> V60 (Nissan VR/VQ)
    @{ Pat = 'gtr|nismo|350z|370z|fairlady|300zx|nissan\s+z|nissan_z|q60|q50|g35|g37|\bz[_\s]?public\b|\bz34\b|\bz33\b'; Cfg = 'V60' }
    # Mercedes V6 (M276) in modern C/E/CLS
    @{ Pat = 'cls63|c-?class|e-?class|amg_one|gtr3?\b'; Cfg = 'V60' }
    # Mitsubishi V6 (3000GT / GTO twin-turbo / FTO 6A12)
    @{ Pat = '3000\s?gt|gto\s+twin|mitsubishi.*gto|\bfto\b'; Cfg = 'V60' }
    # RUF 911-derived flat-6
    @{ Pat = 'ruf|yellowbird'; Cfg = 'Boxer' }
    # Alfa Quadrifoglio (F154 V6)
    @{ Pat = 'quadrifoglio|giulia|stelvio'; Cfg = 'V60' }
    # Lexus SC400 -> 1UZ V8 (cyl=6 here is misdetection but config still meaningful as V60 isn't applicable)
    # Skip — leave as Auto for the misdetected cyl=6
    # AMG One — F1-derived V6 turbo, 90° (treat as V90Even)
    @{ Pat = 'amg\s+one|amg_one'; Cfg = 'V90Even' }
    # Generic supra fallback
    @{ Pat = 'supra'; Cfg = 'Inline' }
    # Generic Austin sports car (Healey etc., usually I6 or I4 — Inline fits both)
    @{ Pat = 'austin'; Cfg = 'Inline' }
    # Lotus Evora V6 (Toyota 2GR-FE)
    @{ Pat = 'evora|exige[\s_]?v6|3[\s_]?eleven'; Cfg = 'V60' }
    # Police Interceptor (Ford Explorer 3.5 EcoBoost V6)
    @{ Pat = 'interceptor|ford_explorer|police.*ford|ford.*police|taurus|nypd|mpw_police|crown_(?:vic|s210)'; Cfg = 'V60' }
    # Holden / Falcon V6
    @{ Pat = 'holden|commodore|falcon'; Cfg = 'V60' }
    # Lexus IS200 (1G-FE I6)
    @{ Pat = 'is200|lexus_is\b'; Cfg = 'Inline' }
    # AMG One -> M11 turbo V6 (75°, treat as V60 for haptics)
    @{ Pat = 'amg_one'; Cfg = 'V60' }
    # Mercedes 190E with cyl=6 (M103 I6)
    @{ Pat = 'mercedes_190|w124|w201'; Cfg = 'Inline' }
    # Bentley Continental cyl=6 (V6 hybrid -- 60°)
    @{ Pat = 'bentley'; Cfg = 'V60' }
    # ALPINA / RUF / smaller sports cars + Inline-6 fallback
    @{ Pat = 'ruf_ctr|alpina|infinity|infiniti'; Cfg = 'Inline' }
    # Default fallback for cyl=6: leave as Auto so Resolver picks V60 (common)
)
$rules8 = @(
    # Ferrari V8 -> flat-plane
    @{ Pat = 'ferrari|458|488|f8|portofino|california|mondial|348|360|430|355|308'; Cfg = 'V8FlatPlane' }
    # McLaren V8 -> flat-plane
    @{ Pat = 'mclaren|570s|650s|720s|750s|mp4|765lt|675lt|600lt|620r|artura|senna|p1\b'; Cfg = 'V8FlatPlane' }
    # Lotus race V8 (DFV) -> flat-plane
    @{ Pat = 'lotus_(?:25|49|72d)|cosworth_dfv|dfv\b'; Cfg = 'V8FlatPlane' }
    # Maserati MC12 / Ferrari-derived V8 -> flat-plane
    @{ Pat = 'mc12|maserati_(?:gt|quattroporte|grancabrio|granturismo)'; Cfg = 'V8FlatPlane' }
    # Mustang GT350 (Voodoo) -> flat-plane
    @{ Pat = 'gt350|voodoo'; Cfg = 'V8FlatPlane' }
    # Everything else cyl=8 -> CrossPlane (American, BMW S6x, Mercedes/AMG, Audi/VW 4.0, Lambo Urus etc.)
    @{ Pat = '.*';  Cfg = 'V8CrossPlane' }
)
$rules10 = @(
    # Lambo / Audi / Lexus LFA / VW Touareg V10 90° -> V90Even
    @{ Pat = '.*';  Cfg = 'V90Even' }
)
$rules12 = @(
    # All cyl=12 default V60 (Ferrari/Lambo/Pagani/AMG/BMW S70/Bentley W12)
    @{ Pat = '.*';  Cfg = 'V60' }
)

function Resolve-Cfg {
    param([string]$CarId, [int]$Cyl)
    $hay = $CarId -replace '_', ' '
    $rules = switch ($Cyl) {
        3  { $rules3 }
        4  { $rules4 }
        5  { $rules5 }
        6  { $rules6 }
        8  { $rules8 }
        10 { $rules10 }
        12 { $rules12 }
        default { return $null }
    }
    foreach ($r in $rules) {
        if ($hay -match $r.Pat) { return $r.Cfg }
    }
    return $null
}

$lines = Get-Content -LiteralPath $Path -Encoding UTF8
$updatedCount = 0
$out = New-Object 'System.Collections.Generic.List[string]'
foreach ($line in $lines) {
    $newLine = $line
    if ($line -match '^\s*\["(?<id>[^"]+)"\]\s*=\s*new BuiltinCarSpec\((?<cyl>\d+)\),(?<rest>.*)$') {
        $carId = $Matches['id']
        $cyl = [int]$Matches['cyl']
        $rest = $Matches['rest']
        $cfg = Resolve-Cfg -CarId $carId -Cyl $cyl
        if ($cfg) {
            $left = $line.Substring(0, $line.IndexOf('= new BuiltinCarSpec'))
            $newLine = "$left= new BuiltinCarSpec($cyl, EngineConfig.$cfg),$rest"
            $updatedCount++
        }
    }
    $out.Add($newLine)
}
Set-Content -LiteralPath $Path -Value $out -Encoding UTF8
Write-Host "Updated $updatedCount entries with explicit EngineConfig."
