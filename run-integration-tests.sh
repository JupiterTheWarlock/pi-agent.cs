#!/bin/bash
set -e
cd /home/gjim258/projects/pi-agent.cs
KEY="$1"
python3 -c "
import json
with open('PiAgent.IntegrationTests/appsettings.json','r') as f:
    cfg = json.load(f)
cfg['LLM']['ApiKey'] = '$KEY'
with open('PiAgent.IntegrationTests/appsettings.json','w') as f:
    json.dump(cfg, f, indent=2)
"
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$PATH:$HOME/.dotnet
rm -rf PiAgent.IntegrationTests/bin PiAgent.IntegrationTests/obj
dotnet test PiAgent.IntegrationTests/PiAgent.IntegrationTests.csproj --verbosity normal
python3 -c "
import json
with open('PiAgent.IntegrationTests/appsettings.json','r') as f:
    cfg = json.load(f)
cfg['LLM']['ApiKey'] = '__NEWAPI_TOKEN_1__'
with open('PiAgent.IntegrationTests/appsettings.json','w') as f:
    json.dump(cfg, f, indent=2)
"
