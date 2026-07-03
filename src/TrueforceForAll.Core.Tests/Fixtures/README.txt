Fixture clips + frozen goldens live here. Make them with:
  dotnet run --project tools/FixturePrep -- info   <capture.csv>
  dotnet run --project tools/FixturePrep -- trim   <capture.csv> <name>.csv <startSec> <endSec>
  dotnet run --project tools/FixturePrep -- freeze <name>.csv
Pairs (<name>.csv + <name>.golden.csv) are auto-discovered by FixtureParityTests.
Keep clips 10-30s. Re-freeze ONLY on deliberate behavior changes, with rationale in the commit.
