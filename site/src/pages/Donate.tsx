import { Button } from "@/components/ui/button";

function PrimaryBtn({ href, children }: { href: string; children: React.ReactNode }) {
  return (
    <Button asChild size="md" variant="primary">
      <a href={href} target="_blank" rel="noopener noreferrer">
        {children}
      </a>
    </Button>
  );
}

function KofiLogo() {
  return (
    <img
      src={import.meta.env.BASE_URL + "kofi-logo.webp"}
      alt="ko-fi"
      className="w-8 h-8 shrink-0 object-contain"
    />
  );
}

function PayPalLogo() {
  return (
    <svg viewBox="0 0 24 24" className="w-8 h-8 shrink-0" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
      <path
        fill="#003087"
        d="M7.076 21.337H2.47a.641.641 0 0 1-.633-.74L4.944 1.5a.77.77 0 0 1 .76-.648h7.542c3.944 0 6.698 1.94 6.698 5.37 0 4.723-3.57 7.123-8.223 7.123H8.058L7.076 21.337z"
      />
      <path
        fill="#0070E0"
        d="M18.974 6.222c-.01.073-.022.148-.036.225-1.273 6.534-5.632 8.796-11.2 8.796H4.867c-.681 0-1.256.494-1.362 1.166l-1.449 9.19-.41 2.6a.641.641 0 0 0 .633.74h5.003a.77.77 0 0 0 .76-.648l.031-.164.942-5.977.061-.33a.77.77 0 0 1 .76-.649h.478c4.067 0 7.251-1.652 8.181-6.431.389-1.995.188-3.66-.84-4.83a4.003 4.003 0 0 0-1.145-.885c-.121-.065-.25-.125-.384-.18"
        transform="translate(0.5 -3)"
      />
    </svg>
  );
}

const options = [
  {
    title: "ko-fi",
    description: "buy me a coffee to support development.",
    url: "https://ko-fi.com/T6T71X9ZAM",
    buttonText: "donate on ko-fi",
    logo: <KofiLogo />,
  },
  {
    title: "paypal",
    description: "send a one-time donation via paypal.",
    url: "https://www.paypal.com/paypalme/9KGFX",
    buttonText: "donate on paypal",
    logo: <PayPalLogo />,
  },
];

export default function Donate() {
  return (
    <div className="py-12">
      <div className="mb-8">
        <h1 className="text-[28px] text-black mb-2">donate</h1>
        <p className="text-black/70 leading-relaxed max-w-[60ch]">
          CyberSnap is free and open source. if you find it useful, consider supporting the project.
        </p>
      </div>

      <div>
        {options.map((option) => (
          <div
            key={option.title}
            className="border-t border-[#DDD5C7] py-6 flex items-center gap-4 flex-wrap"
          >
            {option.logo}
            <div className="flex-1 min-w-0">
              <h2 className="text-[16px] text-black mb-1">{option.title}</h2>
              <p className="text-[14px] text-black/70">{option.description}</p>
            </div>
            <PrimaryBtn href={option.url}>{option.buttonText}</PrimaryBtn>
          </div>
        ))}
      </div>
    </div>
  );
}
