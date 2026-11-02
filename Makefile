# One API, three launch profiles. Each runs in the foreground; give each its own terminal tab.
# Every profile carries its own fake Application Insights inside the process.
#
#   make distro      A  the production set-up, as it is   :5101  (ingestion :5901)
#   make control     B  same settings, Azure Monitor pkg  :5102  (ingestion :5902)
#   make fixed       C  A + the sampler set again         :5103  (ingestion :5903)
#
#   make probe PORT=5101            which sampler the app holds, and what it decided
#   make load  PORT=5101 [N=1000]   send N orders, then read the probe
#   make stats PORT=5101            what the fake Application Insights received
#   make reset PORT=5101            forget what it received

N ?= 1000

.PHONY: build distro control fixed probe load stats reset port clean

build:
	dotnet build TelemetryTestProject.slnx

distro:
	dotnet run --project src/TelemetryTest.Api --launch-profile distro

control:
	dotnet run --project src/TelemetryTest.Api --launch-profile control

fixed:
	dotnet run --project src/TelemetryTest.Api --launch-profile fixed

port:
	@test -n "$(PORT)" || (echo "PORT=5101 (A, distro), 5102 (B, control) or 5103 (C, fixed)" && exit 1)

probe: port
	@curl -s http://localhost:$(PORT)/lab/probe | python3 -m json.tool

load: port
	@scripts/load.sh $(PORT) $(N)

stats: port
	@curl -s http://localhost:$(PORT)/lab/stats

reset: port
	@curl -s -X DELETE http://localhost:$(PORT)/lab/stats && echo "ledger cleared on :$(PORT)"

# Before attaching the project to a ticket: build output is not part of it.
clean:
	rm -rf src/*/bin src/*/obj
