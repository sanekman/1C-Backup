"""Cross-platform guard for the WPF binding that crashed on delivery templates."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET


class ProgressBindingTests(unittest.TestCase):
    def test_destination_progress_does_not_write_to_read_only_projection(self):
        root = ET.parse(Path(__file__).resolve().parents[2] / "src/SPBackup.Desktop/MainWindow.xaml")
        ns = {"w": "http://schemas.microsoft.com/winfx/2006/xaml/presentation"}
        name = "{http://schemas.microsoft.com/winfx/2006/xaml}Name"
        items = next(e for e in root.iter() if e.get(name) == "BackupDestinationProgressList")
        bars = items.findall(".//w:ProgressBar", ns)
        self.assertEqual(1, len(bars))
        self.assertEqual("{Binding Percent, Mode=OneWay}", bars[0].get("Value"))
