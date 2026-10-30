"""Immutable publication must stop before any write when a tag/release/distribution is occupied."""
import importlib.util
import io
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch
import urllib.error

spec=importlib.util.spec_from_file_location('server_release',Path(__file__).resolve().parents[2]/'tools/publish_server_release.py')
module=importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class ReleaseGuards(unittest.TestCase):
    def invoke(self,occupied,tag_sha='abc'):
        writes=[]
        def request(req,timeout=None):
            if req.get_method()!='GET': writes.append(req.full_url); raise AssertionError('Mutation before immutability checks')
            if '/git/ref/tags/' in req.full_url:return io.BytesIO(json.dumps({'object':{'sha':tag_sha}}).encode())
            if occupied in req.full_url:return io.BytesIO(b'{"id":1}')
            raise urllib.error.HTTPError(req.full_url,404,'not found',None,None)
        with patch.dict(module.os.environ,{'GITHUB_REF_NAME':'server-v0.1.0-preview.1','GITHUB_SHA':'abc','GITHUB_TOKEN':'test'}),patch.object(sys,'argv',['publish','--artifacts','unused']),patch.object(module.urllib.request,'urlopen',side_effect=request):
            with self.assertRaises((FileExistsError,RuntimeError)):module.main()
        self.assertEqual([],writes)
    def test_existing_release_is_never_overwritten(self):self.invoke('/releases/tags/')
    def test_existing_distribution_is_never_overwritten(self):self.invoke('/git/ref/heads/')
    def test_mismatched_tag_is_never_retargeted(self):self.invoke('never','other-source')

if __name__=='__main__':unittest.main()
