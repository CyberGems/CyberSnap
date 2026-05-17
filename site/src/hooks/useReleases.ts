import { useState } from "react";

export interface ReleaseAsset {
  name: string;
  browser_download_url: string;
  size: number;
  content_type: string;
}

export interface Release {
  id: number;
  tag_name: string;
  name: string;
  published_at: string;
  body: string;
  html_url: string;
  assets: ReleaseAsset[];
  tarball_url: string;
  zipball_url: string;
}

export function useReleases() {
  const [releases] = useState<Release[]>([]);
  const [loading] = useState(false);

  return { releases, loading };
}
