# Scrubbed real-machine regression fixtures

Each `.json` file here is a **scrubbed regression fixture**: a real PC's raw snapshot written by
`capture-fixture`, reviewed, and kept as evidence that the collectors and the normaliser read real hardware
correctly. The policy is in `CLAUDE.md` ("Fixtures in git") and architecture §13. In short:

- **Raw real-machine captures** (`scan --json` output, screenshots, anything not written by `capture-fixture`)
  never enter git. They stay under `..\research\validation-evidence\`.
- **A scrubbed fixture** may enter git only when all of these hold:
  1. `capture-fixture` wrote it, from a build of a clean commit (`app.commit` is known), and its identifier
     check passed on the PC that was captured;
  2. a person reviewed it by hand for anything the scrub can't know about, including nested strings, the
     decoded EDID and SMBIOS bytes, partition paths, and Steam text;
  3. it is listed in the table below with that review;
  4. `ScrubbedFixtureTests` passes on it. That test checks the schema, the commit, EDID and SMBIOS bytes
     with nothing left to scrub, stand-ins for every opaque id, no user-folder path, and no personal Steam
     value.
- **What a fixture may hold:** hardware and configuration facts that are kept deliberately (models, sizes,
  link states, modes, firmware versions, the SMBIOS strings the scrubber allows). **What it must not hold:**
  serial numbers, container IDs or other unit identifiers, user-folder paths, account identifiers, machine or
  volume GUIDs, and user-chosen text.
- **A PC with Steam:** its list of installed games is personal. Its fixture enters git only with the PC
  owner's consent, recorded in the table.

| Fixture | Machine | Captured from | Windows | Reviewed | Steam | Notes |
|---|---|---|---|---|---|---|
| `PC-000-20260928T223014Z.json` | **Synthetic notebook.** A scrubbed capture of a real notebook whose identity-bearing fields were replaced with invented ones (2026-10-25): manufacturer ACMEPC, product and board names, battery and memory-module parts, the disk model, the PCI subsystem ids and the panel's EDID manufacturer and product code (ACM, 1; block checksum recomputed). Structure, lengths and everything else are the capture's: i7-1165G7, Iris Xe only, one NVMe, an internal 60 Hz 2160 × 1440 panel, 16 GiB in two modules | `1d55402`, published single-file CLI, standard user | Windows 11 Pro 25H2, build 26200, x64 | The capture was reviewed by hand on 2026-09-28 (identifier check passed, 14 kinds; every string; decoded EDID and SMBIOS; partition paths; ids). The replacement keeps `ScrubbedFixtureTests` green | not installed | Not a record of any real machine's model; it proves the collectors and normaliser read this configuration |
