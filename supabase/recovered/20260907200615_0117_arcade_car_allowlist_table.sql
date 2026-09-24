create table if not exists public.arcade_cars (
    game    text    not null,
    car_id  integer not null,
    code    text    not null,
    name    text    not null,
    primary key (game, car_id)
);

comment on table public.arcade_cars is
    'Cars that exist, per game. Generated from Id8CarTable.cs. Bounds submit_arcade_lap_time and feeds the Discord command autocomplete.';

alter table public.arcade_cars enable row level security;

drop policy if exists arcade_cars_readable on public.arcade_cars;
create policy arcade_cars_readable on public.arcade_cars for select using (true);

revoke all on public.arcade_cars from anon, authenticated;
grant select on public.arcade_cars to anon, authenticated;

insert into public.arcade_cars (game, car_id, code, name)
select 'ID8', v.car_id, v.code, v.name
from (values
    (0, 'AE86T', 'TRUENO GT-APEX (AE86)'),
    (1, 'AE86L', 'LEVIN GT-APEX (AE86)'),
    (2, 'AE85L', 'LEVIN SR (AE85)'),
    (3, 'SW20', 'MR2 G-Limited (SW20)'),
    (4, 'SXE10', 'ALTEZZA RS200 (SXE10)'),
    (5, 'ZZW30', 'MR-S (ZZW30)'),
    (6, 'JZA80', 'SUPRA RZ (JZA80)'),
    (7, 'ZN6', '86 GT (ZN6)'),
    (8, 'ZVW30', 'PRIUS (ZVW30)'),
    (9, 'AE86T2', 'TRUENO 2door GT-APEX (AE86)'),
    (10, 'ST205', 'CELICA GT-FOUR (ST205)'),
    (256, 'BNR32', 'SKYLINE GT-R (BNR32)'),
    (257, 'BNR34', 'SKYLINE GT-R (BNR34)'),
    (258, 'S13K', 'SILVIA K''s (S13)'),
    (259, 'S14Q', 'Silvia Q''s (S14)'),
    (260, 'S15', 'Silvia spec-R (S15)'),
    (261, 'RPS13', '180SX TYPE II (RPS13)'),
    (262, 'Z33', 'FAIRLADY Z (Z33)'),
    (263, 'R35', 'GT-R NISMO (R35)'),
    (264, 'ER34', 'SKYLINE 25GT TURBO (ER34)'),
    (512, 'EG6', 'Civic SiR·II (EG6)'),
    (513, 'EK9', 'CIVIC TYPE R (EK9)'),
    (514, 'DC2', 'INTEGRA TYPE R (DC2)'),
    (515, 'AP1', 'S2000 (AP1)'),
    (516, 'NA1', 'NSX (NA1)'),
    (768, 'FC3S', 'RX-7 Infini III (FC3S)'),
    (769, 'FD3S', 'RX-7 Type R (FD3S)'),
    (770, 'SE3P', 'RX-8 Type S (SE3P)'),
    (771, 'NA6CE', 'ROADSTER (NA6CE)'),
    (772, 'NB8C', 'ROADSTER RS (NB8C)'),
    (773, 'FD3S6', 'RX-7 Type RS (FD3S)'),
    (1024, 'GC8S5', 'IMPREZA STi Ver.V (GC8)'),
    (1025, 'GDBF', 'IMPREZA STI (GDBF)'),
    (1026, 'GDBA', 'IMPREZA STi (GDBA)'),
    (1027, 'ZC6', 'BRZ S (ZC6)'),
    (1280, 'CE9A', 'LANCER Evolution III (CE9A)'),
    (1281, 'CN9A', 'LANCER EVOLUTION IV (CN9A)'),
    (1282, 'CT9A9', 'LANCER Evolution IX (CT9A)'),
    (1283, 'CT9A7', 'LANCER EVOLUTION VII (CT9A)'),
    (1284, 'CZ4A', 'LANCER EVOLUTION X (CZ4A)'),
    (1285, 'CP9A5', 'LANCER EVOLUTION V (CP9A)'),
    (1286, 'CP9A6T', 'LANCER EVOLUTION VI (CP9A)'),
    (1536, 'EA11R', 'Cappuccino (EA11R)'),
    (1792, 'RPS13K', 'SILEIGHTY'),
    (2048, 'FD3SC', 'GENKI-7 (FD3S)'),
    (2049, 'EK9C', 'MONSTER CIVIC R (EK9)'),
    (2050, 'AP1C', 'S2000 GT1 (AP1)'),
    (2051, 'JZA80C', 'G-FORCE SUPRA (JZA80 Kai)'),
    (2052, 'NA8CC', 'ROADSTER C-SPEC (NA8C Kai)'),
    (2053, 'NA2C', 'NSX-R GT (NA2)')
) as v(car_id, code, name)
on conflict (game, car_id) do update
    set code = excluded.code, name = excluded.name;