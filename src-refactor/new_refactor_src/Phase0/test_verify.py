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

if __name__=='__main__':unittest.main()
