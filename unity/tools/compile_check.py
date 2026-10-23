#!/usr/bin/env python3
"""Biên dịch nhanh code của package bằng dotnet, không cần mở Unity Editor.

Lấy reference Unity / netstandard từ csproj Unity tự sinh của một project Unity 6 bất kỳ
đã mở ít nhất một lần (có Assembly-CSharp.csproj và Library/), cộng UniTask, Newtonsoft,
TextMeshPro, uGUI, NUnit, TestRunner từ Library của project đó.

    python3 compile_check.py --project ~/Downloads/AbiGames/AntFood --out /tmp/lbcc \\
        Runtime Editor Tests Samples~

In lỗi / cảnh báo CS rồi trả mã thoát khác 0 nếu có lỗi. Tất cả thư mục gộp vào MỘT assembly
nên không kiểm được ranh giới asmdef — Unity mới là nơi kiểm cái đó.
"""
import argparse
import glob
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PACKAGE = os.path.join(os.path.dirname(HERE), 'com.abigames.leaderboard')


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--project', required=True, help='project Unity 6 đã mở (có Assembly-CSharp.csproj + Library)')
    parser.add_argument('--out', required=True, help='thư mục tạm cho csproj và bản build')
    parser.add_argument('folders', nargs='+', help='thư mục con của package cần biên dịch')
    args = parser.parse_args()

    project = os.path.expanduser(args.project)
    csproj = open(os.path.join(project, 'Assembly-CSharp.csproj')).read()
    refs = re.findall(r'<Reference Include="([^"]+)">\s*<HintPath>([^<]+)</HintPath>', csproj)
    unity_refs = [(n, p) for n, p in refs
                  if '/Unity.app/' in p or '\\Editor\\Data\\' in p
                  if 'NetStandard' in p or '/Managed/UnityEngine/' in p or 'UnityEditor' in p]

    lib = os.path.join(project, 'Library')
    def one(pattern: str) -> str:
        found = glob.glob(os.path.join(lib, pattern))
        if not found:
            sys.exit(f'không thấy {pattern} trong {lib}')
        return found[0]

    extra = {
        'UniTask': one('ScriptAssemblies/UniTask.dll'),
        'UnityEngine.UI': one('ScriptAssemblies/UnityEngine.UI.dll'),
        'Unity.TextMeshPro': one('ScriptAssemblies/Unity.TextMeshPro.dll'),
        'UnityEngine.TestRunner': one('ScriptAssemblies/UnityEngine.TestRunner.dll'),
        'UnityEditor.TestRunner': one('ScriptAssemblies/UnityEditor.TestRunner.dll'),
        'Newtonsoft.Json': one('PackageCache/com.unity.nuget.newtonsoft-json@*/Runtime/Newtonsoft.Json.dll'),
        'nunit.framework': one('PackageCache/com.unity.ext.nunit@*/net40/unity-custom/nunit.framework.dll'),
    }

    files = []
    for folder in args.folders:
        files += sorted(glob.glob(os.path.join(PACKAGE, folder, '**', '*.cs'), recursive=True))
    if not files:
        sys.exit('không có file .cs nào')

    compile_items = '\n'.join(f'    <Compile Include="{f}" />' for f in files)
    ref_items = '\n'.join(f'    <Reference Include="{n}"><HintPath>{p}</HintPath></Reference>'
                          for n, p in unity_refs + list(extra.items()))
    out = os.path.abspath(os.path.expanduser(args.out))
    os.makedirs(out, exist_ok=True)
    path = os.path.join(out, 'check.csproj')
    with open(path, 'w') as f:
        f.write(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <AssemblyName>AbiLeaderboardCheck</AssemblyName>
    <EnableDefaultItems>false</EnableDefaultItems>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <NoStandardLibraries>true</NoStandardLibraries>
    <NoStdLib>true</NoStdLib>
    <NoConfig>true</NoConfig>
    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
    <DefineConstants>UNITY_EDITOR;UNITY_2022_3_OR_NEWER;UNITY_6000_0_OR_NEWER;ENABLE_UNITYWEBREQUEST;UNITY_INCLUDE_TESTS;NET_STANDARD_2_1</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
{compile_items}
  </ItemGroup>
  <ItemGroup>
{ref_items}
  </ItemGroup>
</Project>
''')

    result = subprocess.run(
        ['dotnet', 'build', path, '-nologo', '-v', 'q',
         f'-p:OutputPath={out}/bin/', f'-p:BaseIntermediateOutputPath={out}/obj/'],
        capture_output=True, text=True)
    lines = sorted({re.sub(r'\s*\[[^\]]*\.csproj\]$', '', l.replace(PACKAGE + '/', ''))
                    for l in result.stdout.splitlines() if re.search(r'(error|warning) CS\d+', l)})
    for line in lines:
        print(line)
    errors = sum(1 for l in lines if ' error CS' in l)
    print(f'{len(files)} file — {errors} lỗi, {len(lines) - errors} cảnh báo')
    return 1 if errors or result.returncode else 0


if __name__ == '__main__':
    sys.exit(main())
