// Play reports (services/Meitou.Reports): the service's address and the Cloudflare Turnstile site key for the page's form.
// An empty url hides the report buttons; an empty siteKey sends reports without the bot check (the service then must not ask for one).
export const reports = {
  url: '',
  turnstileSiteKey: '',
};
