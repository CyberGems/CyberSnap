const updatedAt = "April 30, 2026";

const thirdPartyServices = [
  "Imgur, ImgBB, Catbox, Litterbox, Gyazo, file.io, Uguu, tmpfiles.org, Gofile, ImgPile, Dropbox, Google Drive, OneDrive, Azure Blob, Immich, FTP, SFTP, WebDAV, S3-compatible storage, and custom HTTP endpoints when you choose one of those upload destinations.",
  "ChatGPT, Claude, Gemini, and Google Lens when you use AI redirect features.",
  "Google Translate when you choose Google translation and provide an API key.",
  "Remove.bg, Photoroom, and DeepAI when you choose those cloud providers for sticker or upscale processing.",
  "Hugging Face and Python package repositories when you install optional local translation, sticker, or upscale models.",
];

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="border-t border-[#DDD5C7] pt-8">
      <h2 className="text-[17px] text-black mb-3">{title}</h2>
      <div className="space-y-3 text-[14px] leading-relaxed text-black/70">
        {children}
      </div>
    </section>
  );
}

export default function Privacy() {
  return (
    <div className="py-12 space-y-8">
      <div className="space-y-3">
        <h1 className="text-[28px] text-black">privacy policy</h1>
        <p className="text-[14px] text-black/50">last updated: {updatedAt}</p>
        <p className="text-[15px] leading-relaxed text-black/70 max-w-[72ch]">
          CyberSnap is a local-first screenshot, OCR, annotation, upload, sticker,
          and recording tool for Windows. CyberSnap does not run an CyberSnap account
          service, does not include advertising, and does not collect analytics
          or telemetry from the desktop app.
        </p>
      </div>

      <Section title="what CyberSnap collects">
        <p>
          The CyberSnap desktop app does not collect personal information for us.
          Captures, recordings, OCR text, color history, settings, logs, and
          search indexes are stored locally on your device when the related
          features are enabled.
        </p>
        </Section>

      <Section title="local files and history">
        <p>
          CyberSnap can save screenshots, stickers, GIFs, videos, OCR history,
          color history, thumbnails, and image-search indexes on your computer.
          By default, saved captures and history are kept in local user folders
          such as Pictures/CyberSnap and Pictures/CyberSnap History.
        </p>
        <p>
          You control local retention in the app. Deleting history in CyberSnap is
          intended to remove the local entries and their managed files.
        </p>
      </Section>

      <Section title="settings and secrets">
        <p>
          CyberSnap stores app settings locally. If you add API keys, access
          tokens, passwords, or upload credentials, CyberSnap stores them in the
          local settings file and protects supported secrets with Windows DPAPI
          for the current Windows user. Exported settings are redacted.
        </p>
        <p>
          Diagnostic logs are written locally and attempt to redact common
          secrets such as API keys, passwords, tokens, and authorization headers.
        </p>
      </Section>

      <Section title="uploads and cloud features">
        <p>
          Screenshots, recordings, stickers, or other files leave your device
          only when you choose an upload destination, enable auto-upload, use a
          cloud processing provider, or use a feature that opens a third-party
          service with your content.
        </p>
        <p>
          When you use an upload destination, the selected file and any required
          credentials are sent to that provider. The provider's own privacy
          policy and retention rules apply. Public or temporary hosting services
          may make uploaded files accessible to anyone with the resulting link.
        </p>
      </Section>

      <Section title="ocr, translation, stickers, and upscaling">
        <p>
          OCR uses the Windows OCR engine locally. Local translation, local
          sticker removal, and local upscaling run on your device after their
          optional runtimes or models are installed.
        </p>
        <p>
          If you choose Google Translate, Remove.bg, Photoroom, or DeepAI,
          CyberSnap sends the text or image needed for that operation to the
          selected service. If you install optional local models or runtimes,
          CyberSnap may download model files or Python packages from their
          upstream hosts.
        </p>
      </Section>

      <Section title="updates and downloads">
        <p>
          CyberSnap can check for new releases and download updates when you
          choose to install them. Installed builds may also use the app's update
          system to retrieve release metadata and update packages.
        </p>
      </Section>

      <Section title="third-party services">
        <p>
          Depending on what you choose to use, CyberSnap may interact with these
          third-party services:
        </p>
        <ul className="space-y-2 list-disc pl-5">
          {thirdPartyServices.map((service) => (
            <li key={service}>{service}</li>
          ))}
        </ul>
        <p>
          Review the privacy policy of any service you configure or open from
          CyberSnap. We do not control how those services process uploaded files,
          text, account tokens, or request metadata.
        </p>
      </Section>

      <Section title="children">
        <p>
          CyberSnap is not directed to children, and we do not knowingly collect
          personal information from children through the desktop app or website.
        </p>
      </Section>

      <Section title="changes">
        <p>
          We may update this policy as CyberSnap changes. The current version will
          be posted on this page with the latest update date.
        </p>
      </Section>

      <Section title="contact">
        <p>
          For privacy questions, contact us through the project's issue tracker.
        </p>
      </Section>
    </div>
  );
}
