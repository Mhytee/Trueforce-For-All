# Apply hand-researched + bake-corrections engine info to BuiltinCarCylinders.cs.
# Sources:
#   - additional_manual.csv : carIds where we manually identified the engine
#     (e.g. "M3 GT2 race S65 V8") via web search before the firing-order
#     system existed.
#   - bake_corrections.csv  : carIds where a prior cyl bake was wrong and
#     the codename in the description fixed it; codename also implies
#     layout (2JZ -> Inline I6, LS -> V8 cross-plane, 13B -> Rotary, etc.).
#   - One web-verified addition: Hennessey Venom F5 Fury is cross-plane
#     pushrod, not flat-plane (I had it wrong in earlier baking).
#
# Idempotent: rewrites only entries that differ from the target spec.

param(
    [string]$Path = "$PSScriptRoot/../src/TrueforceForAll.Plugin/BuiltinCarCylinders.cs"
)

# Map: CarId -> (Cyl, EngineConfig, Comment)
$fixes = @{
    # additional_manual.csv research entries
    'bmw_m3_gt2'                                  = @{ Cyl = 8;  Cfg = 'V8CrossPlane'; Note = 'M3 GT2 race S65 V8 cross-plane' }
    'gmp_e60_m5_ericsson'                         = @{ Cyl = 10; Cfg = 'V90Even';      Note = 'E60 M5 S85 V10 90 deg' }
    'gravygarage_street_omega'                    = @{ Cyl = 6;  Cfg = 'V60';          Note = 'Holden Omega V6 (V8 swaps overridden per-car)' }
    'nohesi_audi_rs5_f5'                          = @{ Cyl = 6;  Cfg = 'V60';          Note = 'Audi RS5 F5, EA839 2.9L V6 BiTurbo' }
    'nohesi_lamborghini_svj_63'                   = @{ Cyl = 12; Cfg = 'V60';          Note = 'Aventador SVJ L539 V12 60 deg' }
    'nohesi_mercedes_c63_amg_w204_blackseries'    = @{ Cyl = 8;  Cfg = 'V8CrossPlane'; Note = 'W204 C63 Black Series M156 NA V8 cross-plane' }
    'nohesi_realistic_bmw_g87_adro_v2'            = @{ Cyl = 6;  Cfg = 'Inline';       Note = 'BMW M2 G87 S58 I6' }
    'nstymobn_v2'                                 = @{ Cyl = 8;  Cfg = 'V8CrossPlane'; Note = 'Cadillac CTS-V LT4 supercharged V8 cross-plane' }
    'rtm_hennessey_venom_f5'                      = @{ Cyl = 8;  Cfg = 'V8CrossPlane'; Note = 'Hennessey Fury 6.6L V8 (web-verified cross-plane pushrod, not flat-plane)' }
    'rtm_volkswagen_touareg_r50_traffic'          = @{ Cyl = 10; Cfg = 'V90Even';      Note = 'VW Touareg R50 5.0L V10 TDI 90 deg' }
    'ld_austin_na'                                = @{ Cyl = 4;  Cfg = 'Inline';       Note = 'LD Austin NA Miata B6ZE I4' }

    # bake_corrections.csv: prior cyl was wrong; codename in description
    # also tells us layout. JZ -> Inline I6, LS/M62 -> V8 cross-plane,
    # 13B/20B/26B -> Rotary, RB -> Inline I6, SR/CA -> Inline I4.
    'tando_buddies_s15'                           = @{ Cyl = 6; Cfg = 'Inline';       Note = 'S15 Silvia, JZ swap (codename)' }
    'gravygarage_street_e36_compact'              = @{ Cyl = 4; Cfg = 'Inline';       Note = 'BMW E36 compact, M42/M44 I4' }
    'gravygarage_street_e36_touring'              = @{ Cyl = 8; Cfg = 'V8CrossPlane'; Note = 'BMW E36 touring, V8 swap (M62/LS)' }
    'vdc_nissan_s15_public_2jz'                   = @{ Cyl = 6; Cfg = 'Inline';       Note = 'S15 with explicit 2JZ swap, I6' }
    'NForce_RX8'                                  = @{ Cyl = 6; Cfg = 'Rotary';       Note = 'RX-8 with 20B 3-rotor swap' }
    'ld_josh_370z_v2'                             = @{ Cyl = 8; Cfg = 'V8CrossPlane'; Note = 'Nissan 370Z with V8 swap' }
    'vdc_bmw_e46_public'                          = @{ Cyl = 6; Cfg = 'Inline';       Note = 'BMW E46, M54/S54 I6' }
    'gravygarage_street_s13_brent'                = @{ Cyl = 6; Cfg = 'Inline';       Note = 'S13 with JZ swap (I6)' }
    'gravygarage_street_s13_tim'                  = @{ Cyl = 6; Cfg = 'Inline';       Note = 'S13 with JZ swap (I6)' }
    'ld_mike_fc3s'                                = @{ Cyl = 4; Cfg = 'Rotary';       Note = 'FC RX-7 13B 2-rotor (cyl=4 equiv)' }
    'gravygarage_street_180sx_corbett'            = @{ Cyl = 6; Cfg = 'Inline';       Note = '180SX with JZ swap (I6)' }
    'gravygarage_street_180sx_meade'              = @{ Cyl = 6; Cfg = 'Inline';       Note = '180SX with JZ swap (I6)' }
    'gd_toyota_supra_gr'                          = @{ Cyl = 6; Cfg = 'Inline';       Note = 'GR Supra A90, B58 I6' }
    'vdc_nissan_silvia_180sx_public'              = @{ Cyl = 4; Cfg = 'Inline';       Note = 'Silvia/180SX stock SR/CA I4' }
    'vdc_nissan_s15_public'                       = @{ Cyl = 6; Cfg = 'Inline';       Note = 'S15 with JZ swap inferred (codename)' }
    'prvvy_e30_widebody_4rotor_tt'                = @{ Cyl = 8; Cfg = 'Rotary';       Note = 'E30 with 26B 4-rotor swap' }
    'gravygarage_street_s14_joel'                 = @{ Cyl = 6; Cfg = 'Inline';       Note = 'S14 with JZ swap (I6)' }
    'vdc_nissan_r33_public'                       = @{ Cyl = 6; Cfg = 'Inline';       Note = 'Skyline R33, RB I6' }
    'vdc_nissan_s14_zenki_public'                 = @{ Cyl = 4; Cfg = 'Inline';       Note = 'S14 zenki SR20 I4' }
}

$lines = Get-Content -LiteralPath $Path -Encoding UTF8
$updated = 0
$alreadyCorrect = 0
$missing = New-Object 'System.Collections.Generic.List[string]'
$out = New-Object 'System.Collections.Generic.List[string]'
$seen = @{}

foreach ($line in $lines) {
    $newLine = $line
    if ($line -match '^(?<lead>\s*)\["(?<id>[^"]+)"\]\s*=\s*new BuiltinCarSpec\((?<cyl>\d+)(?:\s*,\s*EngineConfig\.(?<cfg>\w+))?(?:\s*,\s*true)?\s*\),(?<rest>.*)$') {
        $id = $Matches['id']
        if ($fixes.ContainsKey($id)) {
            $seen[$id] = $true
            $target = $fixes[$id]
            $curCyl = [int]$Matches['cyl']
            $curCfg = $Matches['cfg']
            if ($curCyl -eq $target.Cyl -and $curCfg -eq $target.Cfg) {
                $alreadyCorrect++
            } else {
                $lead = $Matches['lead']
                $padded = '"' + $id + '"'
                $padding = ' ' * [math]::Max(1, 50 - $padded.Length)
                $newLine = "$lead[$padded]$padding= new BuiltinCarSpec($($target.Cyl), EngineConfig.$($target.Cfg)),    // $($target.Note)"
                $updated++
            }
        }
    }
    $out.Add($newLine)
}

Set-Content -LiteralPath $Path -Value $out -Encoding UTF8

# Report
Write-Host "Updated: $updated  Already correct: $alreadyCorrect"
foreach ($k in $fixes.Keys) {
    if (-not $seen.ContainsKey($k)) { $missing.Add($k) }
}
if ($missing.Count -gt 0) {
    Write-Host "WARNING: $($missing.Count) carIds from research data not found in bake:" -ForegroundColor Yellow
    $missing | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}
