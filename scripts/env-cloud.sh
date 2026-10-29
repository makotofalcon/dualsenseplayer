# Source this file before dotnet commands in the prepared Codex cloud environment.
export DOTNET_ROOT=/workspace/.tools/dotnet
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME=/workspace/.cache/dotnet-home
export NUGET_PACKAGES=/workspace/.cache/nuget
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
