import Bar from "../bar/Bar"
import * as React from 'react';
import NewsContainer from "../homepage/NewsContainer";
import News from "../../models/News"
import Tag from "../../models/Tag"
import newsService from "../../services/NewsService"
import tagService from "../../services/TagService";
import { Autocomplete, TextField } from "@mui/material"
import { api } from "../../services/Service";
import NewsService from "../../services/NewsService";
import UserService from "../../services/UserService";

const Personal: React.FC = () => {
    const [newsToShow, setNewsToShow] = React.useState<News[]>([])
    const [myTags, setMyTags] = React.useState<Tag[]>([])
    const initalizeNews = async () => {
        let news: News[];

        try {
            news = await NewsService.GetForYou();
        }
        catch (error: any) {
            console.log('unexpected error in getting personal news: ', error)
            news = []
        }

        const newsObject: Promise<News>[] = (news.map(async (x) => {
            const authorName = (await UserService.GetUserWriterById((x as News).authorId))[0].name;
            return new News(
                (x as News).id,
                (x as News).title,
                (x as News).imageUrl,
                authorName,
                (x as News).summary,
                (x as News).text,
                (x as News).authorId,
                (x as News).viewsCount,
                (x as News).likeCount,
                (x as News).postTime
            )
        })
        )
        const newsRealObject = await Promise.all(newsObject);

        setNewsToShow(newsRealObject)
    }


    const getMyTags = async (): Promise<Tag[]> => {
        let tags: Tag[]

        try {
            tags = await api.get("/tag/getAllTags");
        }
        catch (error: any) {
            console.log('unexpected error in getting personal tags: ', error)
            tags = []
        }

        const tagsObject: Tag[] = tags.map((x) => {
            return new Tag(
                (x as any).id,
                (x as any).name
            )
        })

        return tagsObject
    }

    React.useEffect(() => {
        initalizeNews()
    }, [])

    return (
        <div>
            <Bar />
            <NewsContainer newsList={newsToShow} />
        </div>
    )
}

export default Personal