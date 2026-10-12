import tempfile
import unittest
from pathlib import Path
import verify

class GraphGuardTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.previous = verify.ROOT
        verify.ROOT = Path(self.temp.name)
        (verify.ROOT/'Directory.Packages.props').write_text('<Project/>')
    def tearDown(self):
        verify.ROOT = self.previous
        self.temp.cleanup()
    def project(self,name,refs=''):
        (verify.ROOT/(name+'.csproj')).write_text('<Project><ItemGroup>'+refs+'</ItemGroup></Project>')
    def test_missing_reference_fails(self):
        self.project('A','<ProjectReference Include="missing.csproj"/>')
        with self.assertRaisesRegex(ValueError,'missing/external'):verify.inventory()
    def test_reference_outside_tree_fails(self):
        self.project('A','<ProjectReference Include="../outside.csproj"/>')
        with self.assertRaisesRegex(ValueError,'missing/external'):verify.inventory()
    def test_cycle_fails(self):
        self.project('A','<ProjectReference Include="B.csproj"/>')
        self.project('B','<ProjectReference Include="A.csproj"/>')
        with self.assertRaisesRegex(ValueError,'cycle'):verify.inventory()
    def test_external_link_fails(self):
        self.project('A','<Compile Include="../outside.cs"/>')
        with self.assertRaisesRegex(ValueError,'external compile'):verify.inventory()
    def test_contained_graph_passes(self):
        self.project('A','<ProjectReference Include="B.csproj"/>');self.project('B')
        self.assertEqual(verify.inventory()['projects']['A.csproj']['references'],['B.csproj'])
    def test_runtime_reference_to_executable_fails(self):
        self.project('A','<ProjectReference Include="B.csproj"/>')
        (verify.ROOT/'B.csproj').write_text('<Project><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>')
        with self.assertRaisesRegex(ValueError,'runtime reference to executable'):verify.inventory()
    def test_module_entry_point_or_settings_fails(self):
        folder=verify.ROOT/'Shared/Modules/Api';folder.mkdir(parents=True)
        (folder/'Api.csproj').write_text('<Project/>')
        for name in ['Program.cs','appsettings.json']:
            (folder/name).write_text('{}')
            with self.assertRaisesRegex(ValueError,'host entry point/settings'):verify.inventory()
            (folder/name).unlink()
    def test_executable_module_fails(self):
        folder=verify.ROOT/'Shared/Modules/Api';folder.mkdir(parents=True)
        (folder/'Api.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk.Web"/>')
        with self.assertRaisesRegex(ValueError,'module must be a library'):verify.inventory()
    def test_apphost_requires_metadata_only_references(self):
        self.project('B')
        folder=verify.ROOT/'Full/AppHost';folder.mkdir(parents=True)
        project=folder/'AppHost.csproj'
        project.write_text('<Project><ItemGroup><ProjectReference Include="../../B.csproj"/></ItemGroup></Project>')
        with self.assertRaisesRegex(ValueError,'metadata only'):verify.inventory()
        project.write_text(project.read_text().replace('/></ItemGroup>',' ReferenceOutputAssembly="false"/></ItemGroup>'))
        verify.inventory()
    def test_settings_removal_workaround_fails(self):
        (verify.ROOT/'A.csproj').write_text('<Project><Target Name="RemoveServiceSettingsFromPublish"/></Project>')
        with self.assertRaisesRegex(ValueError,'output-removal workaround'):verify.inventory()

if __name__=='__main__':unittest.main()
