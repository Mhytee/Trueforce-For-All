$root = "D:\SteamLibrary\steamapps\common\assettocorsa\content\cars"
$cryptic = @(
    '38_hb_444','chimmybandido_v2','dzrm8_knight','glo_ss','mtn_victoria','nstymobn_v2',
    'pxw_sl_v4','simhq_bt','snp_zhonghua_zidantou_wangan_spec','VersedSingleCab',
    'tommykaira_zzs_pub','swarm_cowdoy_vf','swarm_lhd_uzi_fc1uz','sjbarzcc_g8',
    'porsche_vision_960_turismo','peugeot_205_gti_1.9_gutmann_t16v_222cv','nypd_ford',
    'tnt_hv_bmw_csi_m6','swarm_narraz_rbs13','swarm_lhd_sc300_suzie','swarm_lhd_foxtrot_foxbody',
    'swarm_lewi_altezza','swarm_Juliett_a70','swarm_maniac_300zx','swarm_charlie_jzx100',
    'swarm_flyby_gs300','swarm_fullagaming_r31','tando_buddies_er34','tando_buddies_verossa',
    'naz_jza80_ridox_modern','lotus_exos_125','lotus_exos_125_s1','jf_mclaren_f1_1994',
    'crown_s210_police','j8_toyota_celica_tuned','toy_celica_cs','toy_celica_rs',
    'trvbl_2105_drift','vallejopd_interceptor','HDC_VW_Caddy','M2_Competition_prvvy_tgn',
    'lotus_elise_sport_190_99','lamborghini_gallardo_superleggera_nasher_Ju'
)
foreach ($c in $cryptic | Sort-Object -Unique) {
    $p = Join-Path $root "$c\ui\ui_car.json"
    if (-not (Test-Path $p)) { Write-Host ("{0,-50} | (no ui_car.json)" -f $c); continue }
    try {
        $raw = Get-Content -Raw -LiteralPath $p
        if ($raw.Length -gt 0 -and $raw[0] -eq [char]0xFEFF) { $raw = $raw.Substring(1) }
        $ui = $raw | ConvertFrom-Json -ErrorAction Stop
        Write-Host ("{0,-50} | {1} - {2}" -f $c, "$($ui.brand)", "$($ui.name)")
    } catch {
        Write-Host ("{0,-50} | (parse error: $($_.Exception.Message))" -f $c)
    }
}
