// Use GitHub Gists as free phone book
const { Octokit } = require('@octokit/rest');

class GitHubPhoneBook {
    constructor(token) {
        this.octokit = new Octokit({ auth: token });
        this.gistId = null;
    }

    async init() {
        // Create or find FyteClub phone book gist
        try {
            const gists = await this.octokit.gists.list();
            const existing = gists.data.find(g => 
                g.files['fyteclub-phonebook.json']
            );
            
            if (existing) {
                this.gistId = existing.id;
            } else {
                const created = await this.octokit.gists.create({
                    files: {
                        'fyteclub-phonebook.json': {
                            content: JSON.stringify({})
                        }
                    },
                    public: false
                });
                this.gistId = created.data.id;
            }
        } catch (error) {
            throw new Error(`GitHub phone book init failed: ${error.message}`);
        }
    }

    async register(playerName, connectionInfo) {
        const gist = await this.octokit.gists.get({ gist_id: this.gistId });
        const data = JSON.parse(gist.data.files['fyteclub-phonebook.json'].content);
        
        data[playerName] = {
            ...connectionInfo,
            timestamp: Date.now()
        };

        await this.octokit.gists.update({
            gist_id: this.gistId,
            files: {
                'fyteclub-phonebook.json': {
                    content: JSON.stringify(data)
                }
            }
        });
    }

    async lookup(playerName) {
        const gist = await this.octokit.gists.get({ gist_id: this.gistId });
        const data = JSON.parse(gist.data.files['fyteclub-phonebook.json'].content);
        
        const info = data[playerName];
        if (!info) return null;
        
        // Check if entry is stale (1 hour)
        if (Date.now() - info.timestamp > 3600000) {
            delete data[playerName];
            await this.octokit.gists.update({
                gist_id: this.gistId,
                files: {
                    'fyteclub-phonebook.json': {
                        content: JSON.stringify(data)
                    }
                }
            });
            return null;
        }
        
        return info;
    }
}

module.exports = GitHubPhoneBook;