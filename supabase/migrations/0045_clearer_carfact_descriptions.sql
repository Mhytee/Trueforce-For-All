-- 0045: clearer car-fact achievement descriptions. "Backs N car-fact consensus values" read
-- as jargon to users; restate in plain language (the achievement is for submitting car facts
-- whose values match the community's agreed consensus).
update public.achievements set description = 'Submitted 3+ car facts the community agreed on'  where key = 'car_fact_contributor';
update public.achievements set description = 'Submitted 25+ car facts the community agreed on' where key = 'car_fact_expert';
