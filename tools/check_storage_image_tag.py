"""Fail closed unless GHCR confirms that a release image tag does not exist."""
import argparse
import base64
import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request

REPOSITORY = 'sanekman/1c-backup-storage'


def check_available(version, username, secret, opener=urllib.request.urlopen):
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+', version):
        raise ValueError('Invalid storage image version')
    if not username or not secret:
        raise ValueError('GHCR credentials are required to check the release tag')
    query = urllib.parse.urlencode({'service': 'ghcr.io', 'scope': 'repository:' + REPOSITORY + ':pull'})
    request = urllib.request.Request('https://ghcr.io/token?' + query)
    credentials = base64.b64encode((username + ':' + secret).encode()).decode()
    request.add_header('Authorization', 'Basic ' + credentials)
    with opener(request, timeout=30) as response:
        bearer = json.load(response)['token']
    request = urllib.request.Request('https://ghcr.io/v2/' + REPOSITORY + '/manifests/' + version, method='HEAD')
    request.add_header('Authorization', 'Bearer ' + bearer)
    request.add_header('Accept', 'application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.docker.distribution.manifest.v2+json')
    try:
        with opener(request, timeout=30) as response:
            status = response.status
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return
        raise RuntimeError('GHCR could not confirm that the tag is available (HTTP ' + str(error.code) + ')') from None
    if status == 200:
        raise RuntimeError('Storage image tag already exists; no overwrite permitted')
    raise RuntimeError('Unexpected GHCR response; image publication stopped')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('version')
    args = parser.parse_args()
    check_available(args.version, os.environ.get('GHCR_USERNAME'), os.environ.get('GHCR_TOKEN'))
