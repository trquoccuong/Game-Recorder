# CS2 capture server on Google Cloud

One Windows Server VM running the CS2 dedicated server (Metamod + `odx_netlog`) and `odx-orchestrator` side by side,
as `docs/README.md` requires: the orchestrator reads the server's demo and plugin log from disk. The Linux deployment in
`deploy/` does not fit CS2 (the plugin is win64 only).

The VM sets itself up from its startup script, without RDP: `startup.ps1` runs `setup.ps1` and `build-package.ps1` side by
side from a bucket, then registers `odx-remote` (`remote-poll.ps1`). Scripts and source come from
`gs://odx-game-server-setup` (the name is written in the scripts).

## Create

```bash
P=odx-game-server R=asia-southeast1 Z=asia-southeast1-b B=gs://odx-game-server-setup
gcloud projects create $P --organization=<org id> && gcloud billing projects link $P --billing-account=<billing id>
gcloud services enable compute.googleapis.com --project=$P
gcloud compute addresses create cs2-capture-ip --region=$R --project=$P
gcloud compute firewall-rules create cs2-capture-players --project=$P --target-tags=cs2-capture \
  --source-ranges=<players' IPs or 0.0.0.0/0> --allow=udp:27015,tcp:5180,udp:12300
gcloud compute firewall-rules create cs2-capture-rdp --project=$P --target-tags=cs2-capture \
  --source-ranges=<your IP>/32,35.235.240.0/20 --allow=tcp:3389
gcloud compute firewall-rules delete default-allow-rdp default-allow-ssh --project=$P --quiet   # open to the internet

gcloud storage buckets create $B --project=$P --location=$R --uniform-bucket-level-access --public-access-prevention
SA=$(gcloud iam service-accounts list --project=$P --format='value(email)' --filter=compute)
for role in objectViewer objectCreator; do
  gcloud storage buckets add-iam-policy-binding $B --member=serviceAccount:$SA --role=roles/storage.$role; done
git archive --format=tar -o /tmp/odx-capture.tar HEAD odx-capture        # from the repository root
gcloud storage cp /tmp/odx-capture.tar deploy/gcp/{setup,build-package,remote-poll}.ps1 $B/

gcloud compute instances create cs2-capture --project=$P --zone=$Z --machine-type=c3-standard-4 \
  --image-family=windows-2022 --image-project=windows-cloud --boot-disk-size=200GB --boot-disk-type=pd-balanced \
  --address=cs2-capture-ip --tags=cs2-capture \
  --scopes=storage-rw,logging-write,monitoring-write,service-management,service-control,trace \
  --metadata-from-file=windows-startup-script-ps1=deploy/gcp/startup.ps1
```

About 50 min to a running server: tools and VS Build Tools ~15 min, CS2 ~74 GB download plus a full verify ~40 min,
then plugin, orchestrator and the players' package. Follow it on the serial port:

```bash
gcloud compute instances get-serial-port-output cs2-capture --zone=$Z --project=$P | grep -a 'odx-'
```

Then: `gs://odx-game-server-setup/packages/odx-capture-0.1.0-win-x64.zip` is the players' package (orchestrator
`http://<static IP>:5180` and the access key preset), sessions appear under `gs://odx-game-server-setup/delivery/`
every 5 min. On the VM: `C:\odx\` (logs\, secrets\ readable by SYSTEM and Administrators only, odx\data\delivery\).

## Run a command on the VM

Upload a `run-once.ps1`; within 30 s `odx-remote` runs it as SYSTEM and uploads its output:

```bash
gcloud storage cp run-once.ps1 gs://odx-game-server-setup/
gcloud storage cat gs://odx-game-server-setup/logs/run-once-<time>.txt | iconv -f UTF-16 -t UTF-8
```

## Lessons from the first deployment

- **CS2 needs `steamclient64.dll` next to `cs2.exe`** on a machine without the Steam client, or it dies at map load:
  `FATAL ERROR: Failed to initialize Steamworks SDK for gameserver. Could not determine Steam client install directory`
  (seen only in the crash dump `game\bin\win64\*.mdmp`). `setup.ps1` copies it, with `tier0_s64.dll` and
  `vstdlib_s64.dll`, from SteamCMD.
- **RCON listens on the NIC's address, not on 127.0.0.1.** `--cs2-rcon 127.0.0.1:27015` is refused, `tv_record` never
  reaches the server and every session gets "SourceTV is not recording". `run-orchestrator.ps1` passes the Ethernet
  address.
- **Metamod makes `csgo\addons\metamod` the engine's write path** (its `gameinfo.gi` line comes first): `console.log`
  and `tv_record` output land there. `setup.ps1` joins `addons\metamod\replays` to `csgo\replays` with a junction.
- **The CS2 download is ~74 GB**, and SteamCMD can sit after "committing" without exiting; check the disk and CPU in
  Cloud Monitoring before deciding it hangs, and read logs instead of waiting (the transcript `C:\odx\logs\setup.log`).
- **Windows PowerShell 5.1:** `Invoke-WebRequest` returns an `application/octet-stream` body as bytes (download it to a
  file instead), `RandomNumberGenerator.Fill` does not exist, and a native command run in a PowerShell console window
  (`cs2.exe`) closes that window when it exits: start it with `Start-Process` and log to a file.
- **Scopes need a stopped VM.** Create the VM with `storage-rw` from the start; changing it later means a stop/start.
- **Nothing to read from outside without a channel**: the serial port only shows what the startup script prints, and a
  child process's redirected output arrives late. Set up the bucket channel (`odx-remote`) first, then debug.
- The players' package is built on the VM (it has .NET and the MSVC tools; MSYS2 is added for ffmpeg), so the access key
  never leaves it; the orchestrator is plain HTTP, so the key travels in clear — restrict `cs2-capture-players` to the
  players' IPs when you can.
- Cost: ~$0.35–0.40/h for c3-standard-4 with the Windows licence; stop the VM between test days.

## Remove

```bash
gcloud projects delete odx-game-server      # everything: VM, disk, IP, firewall, bucket (sessions included)
```
