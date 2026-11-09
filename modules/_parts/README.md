# Parts for large Settings module files

Run workflow `assemble-settings-module` or locally:

```bash
python3 -c "
import base64, pathlib
root = pathlib.Path('modules/_parts')
def assemble(prefix, dest, n):
    data = ''.join((root / f'{prefix}.{i}').read_text() for i in range(n))
    pathlib.Path(dest).write_bytes(base64.b64decode(data))
assemble('SettingsViewModel.cs.b64', 'modules/WinOptimizer.Settings/SettingsViewModel.cs', 5)
assemble('SettingsPage.xaml.b64', 'modules/WinOptimizer.Settings/Views/SettingsPage.xaml', 9)
"
```

Full sources also in project artifacts: `modules-settings-dll.tar.gz`
