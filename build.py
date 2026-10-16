from pathlib import Path
import argparse
import os
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent


def host_path(path):
    value = str(path)
    if os.name == 'nt':
        return value
    if value.startswith('/mnt/') and len(value) > 7 and value[6] == '/':
        return value[5].upper() + ':\\' + value[7:].replace('/', '\\')
    raise ValueError('A Windows-mounted path is required: ' + value)


def local_path(value):
    if os.name != 'nt' and len(value) > 2 and value[1] == ':':
        return Path('/mnt') / value[0].lower() / value[3:].replace('\\', '/')
    return Path(value)


def main():
    parser = argparse.ArgumentParser(description='Build and test the campaign advisor. Tests use mock HTTP; live Gemini calls require a separate explicit smoke command.')
    parser.add_argument('--install', action='store_true', help='Copy the package into its own StreamingAssets folder; does not enable it.')
    parser.add_argument('--sample-save', type=Path, help='Optional local campaign save for a read-only smoke test.')
    parser.add_argument('--campaign-definition', type=Path, help='Campaign definition for --sample-save.')
    parser.add_argument('--query-demo', action='store_true', help='Write a scripted, offline knowledge-query trace using --sample-save.')
    options = parser.parse_args()
    if options.query_demo and not options.sample_save:
        parser.error('--query-demo requires --sample-save and --campaign-definition')
    if bool(options.sample_save) != bool(options.campaign_definition):
        parser.error('--sample-save and --campaign-definition must be supplied together')
    samples = [options.sample_save, options.campaign_definition] if options.sample_save else []
    for sample in samples:
        if not sample.is_file():
            raise FileNotFoundError(sample)
    dotnet = os.environ.get('DOTNET_EXE') or shutil.which('dotnet.exe') or shutil.which('dotnet')
    if not dotnet:
        raise RuntimeError('Windows .NET SDK 10.0.401 was not found.')
    props = ROOT / 'Directory.Build.local.props'
    if not props.exists():
        raise FileNotFoundError('Create Directory.Build.local.props from its example.')
    properties = {element.tag: element.text for element in ET.parse(props).getroot().iter() if element.text and element.text.strip()}
    game = local_path(properties['SeaPowerDir'])
    artifact = ROOT / 'artifacts'
    artifact.mkdir(exist_ok=True)
    env = os.environ.copy()
    settings = {
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
        'DOTNET_CLI_HOME': host_path(artifact / 'dotnet-home'),
        'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE': 'true', 'DOTNET_GENERATE_ASPNET_CERTIFICATE': 'false',
    }
    env.update(settings)
    if os.name != 'nt':
        env['WSLENV'] = ':'.join(filter(None, [env.get('WSLENV', ''), *settings]))

    def run(arguments, name):
        result = subprocess.run([dotnet, *arguments], cwd=ROOT, env=env, capture_output=True, timeout=120)
        log = result.stdout.decode('utf-8-sig', errors='replace') + result.stderr.decode('utf-8-sig', errors='replace')
        (artifact / name).write_text(log, encoding='utf-8')
        print(log.rstrip())
        if result.returncode:
            raise RuntimeError('dotnet failed; see artifacts/' + name)

    for project, log in [('src/SeaPowerAIAdvisor.csproj', 'build.log'), ('tests/SeaPowerAIAdvisor.Tests.csproj', 'test-build.log')]:
        run(['build', host_path(ROOT / project), '-c', 'Release', '--nologo', '--verbosity', 'minimal',
             '-p:UseSharedCompilation=false', '-nodeReuse:false'], log)
    campaign = local_path(properties['AnchorChainDir']).parent / '3813157776' / 'DynamicCampaignMod.dll'
    run([host_path(ROOT / 'tests/bin/Release/net10.0/SeaPowerAIAdvisor.Tests.dll'),
         host_path(campaign), host_path(ROOT / 'src/bin/Release/netstandard2.1/SeaPowerAIAdvisor.dll'),
         host_path(game / 'Sea Power_Data/Managed/Seapower-Scripts.dll'),
         *[host_path(p.resolve()) for p in samples]], 'tests.log')
    if options.query_demo:
        run([host_path(ROOT / 'tests/bin/Release/net10.0/SeaPowerAIAdvisor.Tests.dll'), '--query-demo',
             host_path(campaign), *[host_path(p.resolve()) for p in samples], host_path(artifact / 'query-demo.json')], 'query-demo.log')
    package = ROOT / 'dist/SeaPowerAIAdvisor'
    package.mkdir(parents=True, exist_ok=True)
    shutil.copy2(ROOT / 'src/bin/Release/netstandard2.1/SeaPowerAIAdvisor.dll', package)
    shutil.copy2(ROOT / 'package/_info.ini', package)
    print('Package: ' + str(package))
    if options.install:
        destination = game / 'Sea Power_Data/StreamingAssets/SeaPowerAIAdvisor'
        if not (game / 'Sea Power.exe').is_file():
            raise FileNotFoundError('Sea Power.exe was not found.')
        destination.mkdir(exist_ok=True)
        for source in package.iterdir():
            if source.is_file():
                target = destination / source.name
                if target.exists() and target.read_bytes() == source.read_bytes():
                    continue
                temporary = target.with_name(target.name + '.installing')
                shutil.copy2(source, temporary)
                os.replace(temporary, target)
        print('Installed: ' + str(destination))
        print('Enable Sea Power AI Advisor Prototype in the game mod manager, then restart the game.')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
